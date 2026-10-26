using Orders.Application;
using Orders.Application.Catalog;
using Orders.Application.Pricing;
using Orders.Domain;

namespace Orders.Application.UnitTests;

public sealed class PricingServiceTests
{
    private static readonly Currency _brl = Currency.Create("BRL");
    private static readonly string[] _keyboard = ["KB-1"];
    private static readonly string[] _keyboardAndMouse = ["KB-1", "MS-1"];

    private readonly ICatalogPriceProvider _catalog = Substitute.For<ICatalogPriceProvider>();
    private readonly PricingService _service;

    public PricingServiceTests()
    {
        _service = new PricingService(_catalog, new CartValidator(new CartLineValidator()));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PriceAsync_ValidCart_CallsCatalogOnceAndMapsEveryLine()
    {
        Catalog(Price("KB-1", 19.99m, version: 3), Price("MS-1", 10m, version: 1));

        var result = await _service.PriceAsync(_brl, [new CartLine("KB-1", 2), new CartLine("MS-1", 1)], Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Currency.ShouldBe(_brl);
        result.Value.Lines.Count.ShouldBe(2);
        result.Value.Lines[0].ShouldBe(new PricedLine("KB-1", "Name KB-1", "peripherals", Money.Create(19.99m, _brl), 3, 2));
        result.Value.Lines[1].Quantity.ShouldBe(1);
        await _catalog.Received(1).GetPricesAsync(
            _brl, Arg.Is<IReadOnlyCollection<string>>(skus => skus.SequenceEqual(_keyboardAndMouse)), Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PriceAsync_DuplicateSkus_MergesQuantitiesAndAsksCatalogOnce()
    {
        Catalog(Price("KB-1", 10m));

        var result = await _service.PriceAsync(_brl, [new CartLine("KB-1", 2), new CartLine(" kb-1 ", 3)], Token);

        result.Value.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(5);
        await _catalog.Received(1).GetPricesAsync(
            _brl, Arg.Is<IReadOnlyCollection<string>>(skus => skus.SequenceEqual(_keyboard)), Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PriceAsync_MergedQuantityOverflows_ReturnsValidationError()
    {
        var result = await _service.PriceAsync(_brl, [new CartLine("KB-1", int.MaxValue), new CartLine("KB-1", 1)], Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _catalog.DidNotReceiveWithAnyArgs().GetPricesAsync(default!, default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PriceAsync_MissingSkus_ReturnsUnprocessableListingThem()
    {
        _catalog.GetPricesAsync(_brl, Arg.Any<IReadOnlyCollection<string>>(), Token)
            .Returns(Result.Ok(new CatalogPrices([Price("KB-1", 10m)], ["GONE-1", "GONE-2"])));

        var result = await _service.PriceAsync(_brl, [new CartLine("KB-1", 1), new CartLine("GONE-1", 1), new CartLine("GONE-2", 1)], Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Unprocessable);
        result.Error.Message.ShouldContain("GONE-1");
        result.Error.Message.ShouldContain("GONE-2");
        result.Error.Message.ShouldNotContain("KB-1");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PriceAsync_InactiveSku_ReturnsUnprocessableListingIt()
    {
        Catalog(Price("KB-1", 10m, isActive: false), Price("MS-1", 5m));

        var result = await _service.PriceAsync(_brl, [new CartLine("KB-1", 1), new CartLine("MS-1", 1)], Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Unprocessable);
        result.Error.Message.ShouldContain("KB-1");
        result.Error.Message.ShouldNotContain("MS-1");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PriceAsync_PriceInAnotherCurrency_ReturnsUnprocessableListingIt()
    {
        var usd = Money.Create(5m, Currency.Create("USD"));
        _catalog.GetPricesAsync(_brl, Arg.Any<IReadOnlyCollection<string>>(), Token)
            .Returns(Result.Ok(new CatalogPrices([new CatalogPrice("KB-1", "Keyboard", "peripherals", usd, 1, true)], [])));

        var result = await _service.PriceAsync(_brl, [new CartLine("KB-1", 1)], Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Unprocessable);
        result.Error.Message.ShouldContain("KB-1");
        result.Error.Message.ShouldContain("BRL");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PriceAsync_CatalogOmitsASkuWithoutReportingItMissing_TreatsItAsMissing()
    {
        Catalog(Price("KB-1", 10m));

        var result = await _service.PriceAsync(_brl, [new CartLine("KB-1", 1), new CartLine("MS-1", 1)], Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Unprocessable);
        result.Error.Message.ShouldContain("MS-1");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PriceAsync_MissingAndInactive_ReportsBothInOneError()
    {
        _catalog.GetPricesAsync(_brl, Arg.Any<IReadOnlyCollection<string>>(), Token)
            .Returns(Result.Ok(new CatalogPrices([Price("OFF-1", 1m, isActive: false)], ["GONE-1"])));

        var result = await _service.PriceAsync(_brl, [new CartLine("OFF-1", 1), new CartLine("GONE-1", 1)], Token);

        result.Error!.Message.ShouldContain("GONE-1");
        result.Error.Message.ShouldContain("OFF-1");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PriceAsync_CatalogUnavailable_PassesTheErrorThrough()
    {
        _catalog.GetPricesAsync(_brl, Arg.Any<IReadOnlyCollection<string>>(), Token)
            .Returns(Result.Fail<CatalogPrices>(ErrorKind.CatalogUnavailable, "down"));

        var result = await _service.PriceAsync(_brl, [new CartLine("KB-1", 1)], Token);

        result.Error!.Kind.ShouldBe(ErrorKind.CatalogUnavailable);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PriceAsync_InvalidCart_ReturnsValidationErrorWithoutCallingCatalog()
    {
        await AssertInvalid([]);
        await AssertInvalid([new CartLine("KB-1", 0)]);
        await AssertInvalid([new CartLine("KB-1", -1)]);
        await AssertInvalid([new CartLine(" ", 1)]);
        await AssertInvalid(Enumerable.Range(1, 101).Select(index => new CartLine($"SKU-{index}", 1)).ToList());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PriceAsync_ExactlyOneHundredDistinctSkus_IsAccepted()
    {
        var lines = Enumerable.Range(1, 100).Select(index => new CartLine($"SKU-{index}", 1)).ToList();
        Catalog(lines.Select(line => Price(line.Sku, 1m)).ToArray());

        var result = await _service.PriceAsync(_brl, lines, Token);

        result.Value.Lines.Count.ShouldBe(100);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToOrderItem_PricedLine_CarriesTheSnapshotAndTheLineDiscount()
    {
        var line = new PricedLine("KB-1", "Keyboard", "peripherals", Money.Create(10m, _brl), 4, 3);

        var item = line.ToOrderItem(Money.Create(5m, _brl));

        item.Sku.ShouldBe("KB-1");
        item.CatalogVersion.ShouldBe(4);
        item.Quantity.ShouldBe(3);
        item.LineTotal.Amount.ShouldBe(25m);
    }

    private async Task AssertInvalid(IReadOnlyList<CartLine> lines)
    {
        var result = await _service.PriceAsync(_brl, lines, Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _catalog.DidNotReceiveWithAnyArgs().GetPricesAsync(default!, default!, Token);
    }

    private void Catalog(params CatalogPrice[] prices) =>
        _catalog.GetPricesAsync(_brl, Arg.Any<IReadOnlyCollection<string>>(), Token)
            .Returns(Result.Ok(new CatalogPrices(prices, [])));

    private static CatalogPrice Price(string sku, decimal amount, int version = 1, bool isActive = true) =>
        new(sku, $"Name {sku}", "peripherals", Money.Create(amount, _brl), version, isActive);

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
