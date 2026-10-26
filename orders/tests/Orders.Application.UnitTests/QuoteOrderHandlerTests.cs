using Orders.Application.Catalog;
using Orders.Application.Ordering;
using Orders.Application.Pricing;
using Orders.Domain;

namespace Orders.Application.UnitTests;

public sealed class QuoteOrderHandlerTests
{
    private static readonly DateTimeOffset _now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly Guid _client = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Currency _brl = Currency.Create("BRL");

    private readonly ICatalogPriceProvider _catalog = Substitute.For<ICatalogPriceProvider>();
    private readonly QuoteOrderHandler _handler;

    public QuoteOrderHandlerTests()
    {
        _catalog.GetPricesAsync(_brl, Arg.Any<IReadOnlyCollection<string>>(), Token).Returns(Result.Ok(new CatalogPrices(
            [Price("KB-1", 19.99m, 3), Price("MS-1", 10m, 1)], [])));
        _handler = new QuoteOrderHandler(
            new OrderDrafter(new PricingService(_catalog, new CartValidator(new CartLineValidator())), new FixedTimeProvider(_now)),
            new QuoteOrderQueryValidator());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ValidCart_ReturnsThePricedTotalsAndLines()
    {
        var result = await _handler.HandleAsync(Query(Line("KB-1", 2), Line("MS-1", 1)), Token);

        var quote = result.Value;
        quote.Currency.ShouldBe("BRL");
        quote.ItemCount.ShouldBe(3);
        quote.Subtotal.ShouldBe(49.98m);
        quote.DiscountTotal.ShouldBe(0m);
        quote.Total.ShouldBe(49.98m);
        quote.Items.Select(item => (item.Sku, item.Quantity, item.LineTotal)).ShouldBe([("KB-1", 2, 39.98m), ("MS-1", 1, 10m)]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_DuplicateSkus_AreMergedLikeOnCreate()
    {
        var quote = (await _handler.HandleAsync(Query(Line("kb-1", 1), Line("KB-1", 2)), Token)).Value;

        quote.Items.ShouldHaveSingleItem().Quantity.ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ValidCart_CallsCatalogOnce()
    {
        await _handler.HandleAsync(Query(Line("KB-1", 1), Line("MS-1", 1)), Token);

        await _catalog.Received(1).GetPricesAsync(_brl, Arg.Any<IReadOnlyCollection<string>>(), Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_InvalidCartCurrencyOrClient_ReturnsValidationError()
    {
        (await _handler.HandleAsync(Query() with { Lines = [] }, Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await _handler.HandleAsync(Query(Line("KB-1", 0)), Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await _handler.HandleAsync(Query(Line("KB-1", 1)) with { Currency = "REAL" }, Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await _handler.HandleAsync(Query(Line("KB-1", 1)) with { ClientId = Guid.Empty }, Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await _handler.HandleAsync(Query(Line("KB-1", 1)) with { ClientName = "" }, Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _catalog.DidNotReceiveWithAnyArgs().GetPricesAsync(default!, default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_UnknownSku_ReturnsUnprocessable()
    {
        _catalog.GetPricesAsync(_brl, Arg.Any<IReadOnlyCollection<string>>(), Token).Returns(Result.Ok(new CatalogPrices([], ["GONE-1"])));

        var result = await _handler.HandleAsync(Query(Line("GONE-1", 1)), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Unprocessable);
        result.Error.Message.ShouldContain("GONE-1");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_CatalogUnavailable_ReturnsTheError()
    {
        _catalog.GetPricesAsync(_brl, Arg.Any<IReadOnlyCollection<string>>(), Token)
            .Returns(Result.Fail<CatalogPrices>(ErrorKind.CatalogUnavailable, "down"));

        var result = await _handler.HandleAsync(Query(Line("KB-1", 1)), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.CatalogUnavailable);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_NoCurrency_DefaultsToBrl()
    {
        var quote = (await _handler.HandleAsync(Query(Line("KB-1", 1)) with { Currency = null }, Token)).Value;

        quote.Currency.ShouldBe("BRL");
    }

    private static QuoteOrderQuery Query(params CartLine[] lines) =>
        new(_client, "Ana", OrderChannel.Shop, "BRL", lines.Length == 0 ? [Line("KB-1", 1)] : lines);

    private static CartLine Line(string sku, int quantity) => new(sku, quantity);

    private static CatalogPrice Price(string sku, decimal amount, int version) =>
        new(sku, $"Name {sku}", "peripherals", Money.Create(amount, _brl), version, true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
