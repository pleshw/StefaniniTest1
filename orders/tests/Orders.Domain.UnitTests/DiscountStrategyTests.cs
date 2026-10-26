using Orders.Domain.Discounts;

namespace Orders.Domain.UnitTests;

public sealed class DiscountStrategyTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Percentage_OnTheOrder_RoundsOnceOnTheSubtotal()
    {
        var strategy = Create(DiscountType.Percentage, """{ "percent": 10, "appliesTo": "order" }""");

        var result = strategy.Calculate(Cart(Line("KB-1", 19.99m, 1), Line("MS-1", 0.05m, 1)));

        result.Total.ShouldBe(Brl(2.00m));
        result.Lines.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Percentage_OnEachLine_RoundsOncePerLine()
    {
        var strategy = Create(DiscountType.Percentage, """{ "percent": 10, "appliesTo": "line" }""");

        var result = strategy.Calculate(Cart(Line("A-1", 0.05m, 1), Line("B-1", 0.05m, 1), Line("C-1", 0.05m, 1)));

        result.Lines.Select(line => line.Amount).ShouldBe([Brl(0.01m), Brl(0.01m), Brl(0.01m)]);
        result.Total.ShouldBe(Brl(0.03m));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Percentage_AnyCurrency_UsesTheCartCurrency()
    {
        var usd = Currency.Create("USD");
        var strategy = Create(DiscountType.Percentage, """{ "percent": 50, "appliesTo": "order" }""");

        var result = strategy.Calculate(DiscountCart.Create(usd, [new DiscountLine("KB-1", "peripherals", Money.Create(10m, usd), 1)]));

        result.Total.ShouldBe(Money.Create(5m, usd));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FixedAmount_AmountForTheCartCurrency_IsTheDiscount()
    {
        var strategy = Create(DiscountType.FixedAmount, """{ "amounts": [ { "currency": "USD", "amount": 3 }, { "currency": "BRL", "amount": 15.50 } ] }""");

        strategy.Calculate(Cart(Line("KB-1", 100m, 1))).Total.ShouldBe(Brl(15.50m));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FixedAmount_LargerThanTheSubtotal_NeverGoesBelowZero()
    {
        var strategy = Create(DiscountType.FixedAmount, """{ "amounts": [ { "currency": "BRL", "amount": 50 } ] }""");

        strategy.Calculate(Cart(Line("KB-1", 19.99m, 2))).Total.ShouldBe(Brl(39.98m));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FixedAmount_NoAmountForTheCartCurrency_IsZero()
    {
        var strategy = Create(DiscountType.FixedAmount, """{ "amounts": [ { "currency": "USD", "amount": 5 } ] }""");

        strategy.Calculate(Cart(Line("KB-1", 100m, 1))).IsZero.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuyXGetY_MakesTheCheapestUnitsFree()
    {
        var strategy = Create(DiscountType.BuyXGetY, """{ "buy": 2, "get": 1 }""");

        // 7 units -> two groups of 3 -> 2 free units, the cheapest ones: both cables at 5.
        var result = strategy.Calculate(Cart(Line("KB-1", 100m, 4), Line("MS-1", 10m, 1), Line("CB-1", 5m, 2)));

        result.Lines.Select(line => (line.Sku, line.Amount.Amount)).ShouldBe([("CB-1", 10m)]);
        result.Total.ShouldBe(Brl(10m));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuyXGetY_FreeUnitsSpanSeveralLines_TakesThemCheapestFirst()
    {
        var strategy = Create(DiscountType.BuyXGetY, """{ "buy": 1, "get": 1 }""");

        var result = strategy.Calculate(Cart(Line("KB-1", 100m, 3), Line("MS-1", 10m, 1), Line("CB-1", 5m, 2)));

        // 6 units -> 3 free: both cables, then the mouse.
        result.Lines.Select(line => (line.Sku, line.Amount.Amount)).ShouldBe([("CB-1", 10m), ("MS-1", 10m)]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuyXGetY_RestrictedToSkus_CountsOnlyThoseUnits()
    {
        var strategy = Create(DiscountType.BuyXGetY, """{ "buy": 1, "get": 1, "skus": ["kb-1"] }""");

        var result = strategy.Calculate(Cart(Line("KB-1", 100m, 2), Line("CB-1", 5m, 9)));

        result.Lines.ShouldHaveSingleItem().ShouldBe(new DiscountLineAmount("KB-1", Brl(100m)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuyXGetY_NotEnoughUnits_IsZero()
    {
        var strategy = Create(DiscountType.BuyXGetY, """{ "buy": 3, "get": 1 }""");

        strategy.Calculate(Cart(Line("KB-1", 100m, 3))).IsZero.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuyXGetY_HugeQuantities_DoNotOverflow()
    {
        var strategy = Create(DiscountType.BuyXGetY, """{ "buy": 1, "get": 1 }""");

        var result = strategy.Calculate(Cart(Line("A-1", 0.01m, int.MaxValue), Line("B-1", 0.01m, int.MaxValue)));

        result.Total.ShouldBe(Brl(0.01m * int.MaxValue));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(99.99, 0)]
    [InlineData(100, 5)]
    [InlineData(499.99, 25)]
    [InlineData(500, 50)]
    [InlineData(2000, 200)]
    public void Tiered_UsesTheHighestBandTheSubtotalReaches(decimal subtotal, decimal discount)
    {
        var strategy = Create(DiscountType.Tiered, """
            { "currency": "BRL", "tiers": [ { "minSubtotal": 500, "percent": 10 }, { "minSubtotal": 100, "percent": 5 } ] }
            """);

        strategy.Calculate(Cart(Line("KB-1", subtotal, 1))).Total.ShouldBe(Brl(discount));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Tiered_RoundsAwayFromZero()
    {
        var strategy = Create(DiscountType.Tiered, """{ "currency": "BRL", "tiers": [ { "minSubtotal": 0, "percent": 5 } ] }""");

        strategy.Calculate(Cart(Line("KB-1", 0.10m, 1))).Total.ShouldBe(Brl(0.01m));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Tiered_CartInAnotherCurrency_IsZero()
    {
        var usd = Currency.Create("USD");
        var strategy = Create(DiscountType.Tiered, """{ "currency": "BRL", "tiers": [ { "minSubtotal": 0, "percent": 5 } ] }""");

        strategy.Calculate(DiscountCart.Create(usd, [new DiscountLine("KB-1", "peripherals", Money.Create(100m, usd), 1)])).IsZero.ShouldBeTrue();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(DiscountType.Percentage, """{ "percent": 10, "appliesTo": "order", "extra": 1 }""")]
    [InlineData(DiscountType.Percentage, """{ "appliesTo": "order" }""")]
    [InlineData(DiscountType.Percentage, """{ "percent": 0, "appliesTo": "order" }""")]
    [InlineData(DiscountType.Percentage, """{ "percent": 100.01, "appliesTo": "order" }""")]
    [InlineData(DiscountType.Percentage, """{ "percent": 10, "appliesTo": "basket" }""")]
    [InlineData(DiscountType.Percentage, """{ "percent": 10, "appliesTo": 0 }""")]
    [InlineData(DiscountType.Percentage, """{ "percent": "10", "appliesTo": "order" }""")]
    [InlineData(DiscountType.FixedAmount, """{ "amounts": [] }""")]
    [InlineData(DiscountType.FixedAmount, """{ "amounts": [ { "currency": "BRL", "amount": 0 } ] }""")]
    [InlineData(DiscountType.FixedAmount, """{ "amounts": [ { "currency": "REAL", "amount": 5 } ] }""")]
    [InlineData(DiscountType.FixedAmount, """{ "amounts": [ { "currency": "BRL", "amount": 5 }, { "currency": "brl", "amount": 6 } ] }""")]
    [InlineData(DiscountType.BuyXGetY, """{ "buy": 0, "get": 1 }""")]
    [InlineData(DiscountType.BuyXGetY, """{ "buy": 1, "get": 0 }""")]
    [InlineData(DiscountType.BuyXGetY, """{ "buy": 1, "get": 1, "skus": [] }""")]
    [InlineData(DiscountType.BuyXGetY, """{ "buy": 1, "get": 1, "skus": [" "] }""")]
    [InlineData(DiscountType.Tiered, """{ "currency": "BRL", "tiers": [] }""")]
    [InlineData(DiscountType.Tiered, """{ "tiers": [ { "minSubtotal": 0, "percent": 5 } ] }""")]
    [InlineData(DiscountType.Tiered, """{ "currency": "BRL", "tiers": [ { "minSubtotal": -1, "percent": 5 } ] }""")]
    [InlineData(DiscountType.Tiered, """{ "currency": "BRL", "tiers": [ { "minSubtotal": 10, "percent": 5 }, { "minSubtotal": 10, "percent": 6 } ] }""")]
    [InlineData(DiscountType.Tiered, """{ "currency": "BRL", "tiers": [ { "minSubtotal": 10, "percent": 101 } ] }""")]
    [InlineData(DiscountType.Tiered, "not json")]
    [InlineData(DiscountType.Tiered, "[]")]
    public void Validate_InvalidParameters_ReportsAndCreateThrows(DiscountType type, string parameters)
    {
        DiscountStrategies.Validate(type, parameters).IsValid.ShouldBeFalse();
        Should.Throw<DomainException>(() => DiscountStrategies.Create(type, parameters));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(DiscountType.Percentage, """{ "appliesTo": "line", "percent": 12.5 }""")]
    [InlineData(DiscountType.FixedAmount, """{ "amounts": [ { "currency": "BRL", "amount": 5 } ] }""")]
    [InlineData(DiscountType.BuyXGetY, """{ "buy": 2, "get": 1, "skus": ["KB-1"] }""")]
    [InlineData(DiscountType.Tiered, """{ "currency": "BRL", "tiers": [ { "minSubtotal": 0, "percent": 5 } ] }""")]
    public void Validate_ValidParameters_PassesAndCreatesTheStrategyOfThatType(DiscountType type, string parameters)
    {
        DiscountStrategies.Validate(type, parameters).IsValid.ShouldBeTrue();
        DiscountStrategies.Create(type, parameters).Type.ShouldBe(type);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DiscountCart_LineInAnotherCurrency_IsRejected()
    {
        Should.Throw<DomainException>(() => DiscountCart.Create(OrderBuilder.Brl, [new DiscountLine("KB-1", "x", OrderBuilder.UsdAmount(1m), 1)]));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DiscountCart_DuplicateSkuOrBadQuantity_IsRejected()
    {
        Should.Throw<DomainException>(() => Cart(Line("KB-1", 1m, 1), Line("KB-1", 1m, 1)));
        Should.Throw<DomainException>(() => Cart(Line("KB-1", 1m, 0)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DiscountCart_Subtotal_SumsTheLines()
    {
        var cart = Cart(Line("KB-1", 19.99m, 2), Line("MS-1", 10m, 1));

        cart.Subtotal.ShouldBe(Brl(49.98m));
        cart.ItemCount.ShouldBe(3);
    }

    private static IDiscountStrategy Create(DiscountType type, string parameters) => DiscountStrategies.Create(type, parameters);

    private static DiscountCart Cart(params DiscountLine[] lines) => DiscountCart.Create(OrderBuilder.Brl, lines);

    private static DiscountLine Line(string sku, decimal price, int quantity) => new(sku, "peripherals", Brl(price), quantity);

    private static Money Brl(decimal amount) => OrderBuilder.BrlAmount(amount);
}
