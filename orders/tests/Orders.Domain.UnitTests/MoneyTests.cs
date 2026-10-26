namespace Orders.Domain.UnitTests;

public sealed class MoneyTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(1.005, 1.01)]
    [InlineData(1.004, 1.00)]
    [InlineData(2.675, 2.68)]
    public void Create_MoreThanTwoDecimals_RoundsAwayFromZero(double amount, double expected)
    {
        var money = Money.Create((decimal)amount, OrderBuilder.Brl);

        money.Amount.ShouldBe((decimal)expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_NegativeAmount_Throws()
    {
        Should.Throw<DomainException>(() => Money.Create(-0.01m, OrderBuilder.Brl));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AddAndSubtract_SameCurrency_ReturnTheExactSum()
    {
        var ten = OrderBuilder.BrlAmount(10m);

        ten.Add(OrderBuilder.BrlAmount(2.5m)).Amount.ShouldBe(12.5m);
        ten.Subtract(OrderBuilder.BrlAmount(2.5m)).Amount.ShouldBe(7.5m);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Subtract_MoreThanTheAmount_Throws()
    {
        Should.Throw<DomainException>(() => OrderBuilder.BrlAmount(1m).Subtract(OrderBuilder.BrlAmount(2m)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Combine_DifferentCurrencies_Throws()
    {
        Should.Throw<DomainException>(() => OrderBuilder.BrlAmount(1m).Add(OrderBuilder.UsdAmount(1m)));
        Should.Throw<DomainException>(() => OrderBuilder.BrlAmount(1m).Subtract(OrderBuilder.UsdAmount(1m)));
        Should.Throw<DomainException>(() => OrderBuilder.BrlAmount(1m).IsGreaterThan(OrderBuilder.UsdAmount(1m)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Multiply_Quantity_RoundsOnce()
    {
        OrderBuilder.BrlAmount(0.335m).Multiply(3).Amount.ShouldBe(1.02m);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsGreaterThan_Amounts_ComparesThem()
    {
        OrderBuilder.BrlAmount(2m).IsGreaterThan(OrderBuilder.BrlAmount(1m)).ShouldBeTrue();
        OrderBuilder.BrlAmount(1m).IsGreaterThan(OrderBuilder.BrlAmount(1m)).ShouldBeFalse();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(" brl ", "BRL")]
    [InlineData("usd", "USD")]
    public void Currency_Create_NormalizesTheCode(string input, string expected)
    {
        Currency.Create(input).Code.ShouldBe(expected);
        Currency.Create(input).ToString().ShouldBe(expected);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BR")]
    [InlineData("BRLL")]
    [InlineData("B1L")]
    public void Currency_Create_InvalidCode_Throws(string? input)
    {
        Should.Throw<DomainException>(() => Currency.Create(input));
    }
}
