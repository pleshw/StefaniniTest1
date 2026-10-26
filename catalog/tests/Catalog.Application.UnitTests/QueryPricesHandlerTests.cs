using Catalog.Application.Products;
using Catalog.Domain;

namespace Catalog.Application.UnitTests;

public sealed class QueryPricesHandlerTests
{
    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly QueryPricesHandler _handler;

    public QueryPricesHandlerTests()
    {
        _handler = new QueryPricesHandler(_repository, ProductTestData.Currencies);
        _repository.GetManyAsync(Arg.Any<IReadOnlyCollection<Sku>>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_FoundSkus_ReturnsUnitPriceInRequestOrderWithOneRepositoryCall()
    {
        Stored(Summary("B", "Beta"), Summary("A", "Alpha"));

        var result = await _handler.HandleAsync(new PriceQuery("usd", ["a", "B"]), Token);

        var response = result.Value;
        response.Items.Select(item => item.Sku).ShouldBe(["A", "B"]);
        response.Items[0].ShouldBe(new PriceQueryItem("A", "Alpha", "peripherals", 20m, "USD", 1, true));
        response.Missing.ShouldBeEmpty();
        await _repository.Received(1).GetManyAsync(
            Arg.Is<IReadOnlyCollection<Sku>>(skus => string.Join(",", skus.Select(sku => sku.Value)) == "A,B"), Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_UnknownSku_IsListedAsMissing()
    {
        Stored(Summary("A", "Alpha"));

        var result = await _handler.HandleAsync(new PriceQuery("BRL", ["A", "NOPE"]), Token);

        result.Value.Items.Select(item => item.Sku).ShouldBe(["A"]);
        result.Value.Missing.ShouldBe(["NOPE"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ProductWithoutPriceInCurrency_IsListedAsMissing()
    {
        Stored(Summary("A", "Alpha", ProductTestData.Brl(10m)));

        var result = await _handler.HandleAsync(new PriceQuery("USD", ["A"]), Token);

        result.Value.Items.ShouldBeEmpty();
        result.Value.Missing.ShouldBe(["A"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_InactiveProduct_IsReturnedFlaggedInactive()
    {
        Stored(Summary("A", "Alpha", isActive: false));

        var result = await _handler.HandleAsync(new PriceQuery("BRL", ["A"]), Token);

        result.Value.Items.Single().IsActive.ShouldBeFalse();
        result.Value.Missing.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ExactlyMaxSkus_IsAccepted()
    {
        var skus = Enumerable.Range(1, QueryPricesHandler.MaxSkus).Select(index => $"S{index}").ToList();

        var result = await _handler.HandleAsync(new PriceQuery("BRL", skus), Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Missing.Count.ShouldBe(100);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_NoSkus_ReturnsValidationError()
    {
        await AssertInvalid(new PriceQuery("BRL", []));
        await AssertInvalid(new PriceQuery("BRL", null));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_MoreThanMaxSkus_ReturnsValidationError()
    {
        var skus = Enumerable.Range(1, QueryPricesHandler.MaxSkus + 1).Select(index => $"S{index}").ToList();

        await AssertInvalid(new PriceQuery("BRL", skus));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_DuplicateSkusIgnoringCase_ReturnsValidationError()
    {
        await AssertInvalid(new PriceQuery("BRL", ["abc", "ABC"]));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_InvalidSku_ReturnsValidationError()
    {
        await AssertInvalid(new PriceQuery("BRL", ["A", " "]));
        await AssertInvalid(new PriceQuery("BRL", [new string('A', 33)]));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EUR")]
    public async Task HandleAsync_MissingOrUnsupportedCurrency_ReturnsValidationError(string? currency)
    {
        await AssertInvalid(new PriceQuery(currency, ["A"]));
    }

    private async Task AssertInvalid(PriceQuery query)
    {
        var result = await _handler.HandleAsync(query, Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _repository.DidNotReceiveWithAnyArgs().GetManyAsync(default!, Token);
    }

    private void Stored(params ProductSummary[] summaries) =>
        _repository.GetManyAsync(Arg.Any<IReadOnlyCollection<Sku>>(), Arg.Any<CancellationToken>()).Returns(summaries);

    private static ProductSummary Summary(string sku, string name, Money? price = null, bool isActive = true) =>
        new(
            Sku.Create(sku),
            name,
            "peripherals",
            isActive,
            1,
            price is null ? [ProductTestData.Brl(10m), ProductTestData.Usd(20m)] : [price]);

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
