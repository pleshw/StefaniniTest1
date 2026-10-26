using Catalog.Application.Products;

namespace Catalog.Application.UnitTests;

public sealed class ListProductsHandlerTests
{
    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly ListProductsHandler _handler;

    public ListProductsHandlerTests()
    {
        _handler = new ListProductsHandler(_repository, ProductTestData.Currencies);
        _repository.SearchAsync(Arg.Any<ProductSearch>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_NoFilters_SearchesWithDefaultLimitPlusOneAndNoCursor()
    {
        var result = await _handler.HandleAsync(new ListProductsQuery(null, null, null, null, null), Token);

        result.IsSuccess.ShouldBeTrue();
        await _repository.Received(1).SearchAsync(
            Arg.Is<ProductSearch>(search =>
                search.Text == null && search.Category == null && search.After == null && search.Limit == 21),
            Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_TextAndCategory_ForwardsThemToTheRepository()
    {
        await _handler.HandleAsync(new ListProductsQuery("key", "peripherals", null, null, 5), Token);

        await _repository.Received(1).SearchAsync(
            Arg.Is<ProductSearch>(search => search.Text == "key" && search.Category == "peripherals" && search.Limit == 6),
            Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_CurrencyRequested_ReturnsPriceInThatCurrency()
    {
        Returns(ProductTestData.Product("A"));

        var result = await _handler.HandleAsync(new ListProductsQuery(null, null, "usd", null, null), Token);

        var item = result.Value.Items.ShouldHaveSingleItem();
        item.Price.ShouldBe(new PriceDto(20m, "USD"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_NoCurrencyRequested_UsesTheDefaultCurrency()
    {
        Returns(ProductTestData.Product("A"));

        var result = await _handler.HandleAsync(new ListProductsQuery(null, null, null, null, null), Token);

        result.Value.Items.Single().Price.ShouldBe(new PriceDto(100m, "BRL"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ProductWithoutPriceInCurrency_ListsItWithNullPrice()
    {
        Returns(ProductTestData.Product("A", "Keyboard", ProductTestData.Brl(10m)));

        var result = await _handler.HandleAsync(new ListProductsQuery(null, null, "USD", null, null), Token);

        result.Value.Items.Single().Price.ShouldBeNull();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("EUR")]
    [InlineData("XX")]
    public async Task HandleAsync_UnknownCurrency_ReturnsValidationError(string currency)
    {
        var result = await _handler.HandleAsync(new ListProductsQuery(null, null, currency, null, null), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _repository.DidNotReceiveWithAnyArgs().SearchAsync(default!, Token);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task HandleAsync_LimitOutOfRange_ReturnsValidationError(int limit)
    {
        var result = await _handler.HandleAsync(new ListProductsQuery(null, null, null, null, limit), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_MaxLimit_IsAccepted()
    {
        var result = await _handler.HandleAsync(new ListProductsQuery(null, null, null, null, 100), Token);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_MalformedCursor_ReturnsValidationError()
    {
        var result = await _handler.HandleAsync(new ListProductsQuery(null, null, null, "not-a-cursor", null), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ValidCursor_IsDecodedIntoTheSearch()
    {
        var cursor = new ProductCursor("Keyboard", "A").Encode();

        await _handler.HandleAsync(new ListProductsQuery(null, null, null, cursor, null), Token);

        await _repository.Received(1).SearchAsync(
            Arg.Is<ProductSearch>(search => search.After == new ProductCursor("Keyboard", "A")), Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_MoreRowsThanLimit_TrimsThemAndReturnsCursorOfTheLastItem()
    {
        Returns(
            ProductTestData.Product("A", "Alpha"),
            ProductTestData.Product("B", "Beta"),
            ProductTestData.Product("C", "Gamma"));

        var result = await _handler.HandleAsync(new ListProductsQuery(null, null, null, null, 2), Token);

        result.Value.Items.Select(item => item.Sku).ShouldBe(["A", "B"]);
        result.Value.Next.ShouldBe(new ProductCursor("Beta", "B").Encode());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_NoMoreRows_ReturnsNoCursor()
    {
        Returns(ProductTestData.Product("A", "Alpha"), ProductTestData.Product("B", "Beta"));

        var result = await _handler.HandleAsync(new ListProductsQuery(null, null, null, null, 2), Token);

        result.Value.Items.Count.ShouldBe(2);
        result.Value.Next.ShouldBeNull();
    }

    private void Returns(params Domain.Product[] products) =>
        _repository.SearchAsync(Arg.Any<ProductSearch>(), Arg.Any<CancellationToken>()).Returns(products);

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
