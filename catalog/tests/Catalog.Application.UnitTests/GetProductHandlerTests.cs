using Catalog.Application.Products;
using Catalog.Domain;

namespace Catalog.Application.UnitTests;

public sealed class GetProductHandlerTests
{
    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly GetProductHandler _handler;

    public GetProductHandlerTests()
    {
        _handler = new GetProductHandler(_repository, ProductTestData.Currencies);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ExistingSku_ReturnsProductWithVersionAndPrice()
    {
        _repository.GetBySkuAsync(Sku.Create("A"), Token).Returns(ProductTestData.Product("A"));

        var result = await _handler.HandleAsync(new GetProductQuery("a", "USD"), Token);

        result.Value.Sku.ShouldBe("A");
        result.Value.Version.ShouldBe(1);
        result.Value.Price.ShouldBe(new PriceDto(20m, "USD"));
        result.Value.Attributes["layout"].ShouldBe("abnt2");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_NoCurrency_UsesTheDefaultCurrency()
    {
        _repository.GetBySkuAsync(Sku.Create("A"), Token).Returns(ProductTestData.Product("A"));

        var result = await _handler.HandleAsync(new GetProductQuery("A", null), Token);

        result.Value.Price.ShouldBe(new PriceDto(100m, "BRL"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_MissingSku_ReturnsNotFound()
    {
        _repository.GetBySkuAsync(Arg.Any<Sku>(), Token).Returns((Product?)null);

        var result = await _handler.HandleAsync(new GetProductQuery("NOPE", null), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_SkuTooLong_ReturnsValidationError()
    {
        var result = await _handler.HandleAsync(new GetProductQuery(new string('A', 33), null), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _repository.DidNotReceiveWithAnyArgs().GetBySkuAsync(default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_UnknownCurrency_ReturnsValidationError()
    {
        var result = await _handler.HandleAsync(new GetProductQuery("A", "EUR"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
