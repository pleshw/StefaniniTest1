namespace Catalog.Domain.UnitTests;

public sealed class ProductReplaceTests
{
    private static readonly IReadOnlyDictionary<string, string> _noAttributes = new Dictionary<string, string>();

    [Fact]
    [Trait("Category", "Unit")]
    public void Replace_ChangedContentAndPrices_BumpsVersionOnceAndRaisesOneEventPerChangedPrice()
    {
        var product = new ProductBuilder().WithPrices(ProductBuilder.Brl(100m), ProductBuilder.Usd(20m)).Build();

        product.Replace(
            "Mechanical keyboard",
            "New description",
            "office",
            new Dictionary<string, string> { ["layout"] = "us" },
            [ProductBuilder.Brl(120m), ProductBuilder.Usd(20m), Money.Create(3m, Currency.Create("EUR"))]);

        product.Name.ShouldBe("Mechanical keyboard");
        product.Description.ShouldBe("New description");
        product.Category.ShouldBe("office");
        product.Attributes["layout"].ShouldBe("us");
        product.Version.ShouldBe(2);
        product.Prices.Count.ShouldBe(3);
        var events = product.DomainEvents.OfType<PriceChanged>().ToList();
        events.Count.ShouldBe(2);
        events.ShouldAllBe(raised => raised.Version == 2);
        events.Single(raised => raised.NewPrice.Currency.Code == "BRL").OldPrice.ShouldBe(ProductBuilder.Brl(100m));
        events.Single(raised => raised.NewPrice.Currency.Code == "EUR").OldPrice.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Replace_SameContent_ChangesNothing()
    {
        var product = new ProductBuilder().Build();

        product.Replace("Keyboard", "A keyboard", "peripherals", new Dictionary<string, string> { ["layout"] = "abnt2" }, [ProductBuilder.Brl(100m)]);

        product.Version.ShouldBe(1);
        product.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Replace_CurrencyLeftOut_DropsItsPriceWithoutAnEvent()
    {
        var product = new ProductBuilder().WithPrices(ProductBuilder.Brl(100m), ProductBuilder.Usd(20m)).Build();

        product.Replace("Keyboard", "A keyboard", "peripherals", new Dictionary<string, string> { ["layout"] = "abnt2" }, [ProductBuilder.Brl(100m)]);

        product.Prices.Single().Currency.Code.ShouldBe("BRL");
        product.Version.ShouldBe(2);
        product.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Replace_NullDescriptionAndAttributes_UseEmptyValues()
    {
        var product = new ProductBuilder().Build();

        product.Replace("Keyboard", null, "peripherals", null, [ProductBuilder.Brl(100m)]);

        product.Description.ShouldBeEmpty();
        product.Attributes.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Replace_InvalidInput_ThrowsAndLeavesTheProductUntouched()
    {
        var product = new ProductBuilder().Build();

        Should.Throw<DomainException>(() => product.Replace(" ", "d", "c", _noAttributes, [ProductBuilder.Brl(1m)]));
        Should.Throw<DomainException>(() => product.Replace("n", "d", " ", _noAttributes, [ProductBuilder.Brl(1m)]));
        Should.Throw<DomainException>(() => product.Replace("n", "d", "c", _noAttributes, [ProductBuilder.Brl(1m), ProductBuilder.Brl(2m)]));

        product.Name.ShouldBe("Keyboard");
        product.Version.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Replace_DeactivatedProductWithChangedPrice_Throws()
    {
        var product = new ProductBuilder().Build();
        product.Deactivate();

        Should.Throw<DomainException>(() =>
            product.Replace("Keyboard", "A keyboard", "peripherals", _noAttributes, [ProductBuilder.Brl(1m)]));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Replace_DeactivatedProductWithSamePrices_UpdatesTheOtherFieldsAndStaysInactive()
    {
        var product = new ProductBuilder().Build();
        product.Deactivate();

        product.Replace("Renamed", "A keyboard", "peripherals", _noAttributes, [ProductBuilder.Brl(100m)]);

        product.Name.ShouldBe("Renamed");
        product.IsActive.ShouldBeFalse();
        product.Version.ShouldBe(3);
    }
}
