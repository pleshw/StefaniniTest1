using Catalog.Application.Products;
using Catalog.Domain;

namespace Catalog.Application.UnitTests;

public sealed class DeactivateProductHandlerTests
{
    private static readonly DateTimeOffset _now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly DeactivateProductHandler _handler;

    public DeactivateProductHandlerTests()
    {
        _handler = new DeactivateProductHandler(_repository, new FixedTimeProvider(_now), new DeactivateProductCommandValidator());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ActiveProduct_SavesItDeactivatedWithoutPriceHistory()
    {
        Existing();

        var result = await _handler.HandleAsync(Command(), Token);

        result.Value.IsActive.ShouldBeFalse();
        result.Value.Version.ShouldBe(2);
        await _repository.Received(1).SaveAsync(
            Arg.Is<Product>(product => !product.IsActive),
            1,
            Arg.Is<AuditStamp>(audit => audit.UpdatedBy == "admin-1" && audit.UpdatedAt == _now),
            Arg.Is<IReadOnlyCollection<PriceHistoryEntry>>(history => history.Count == 0),
            Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_NoIfMatch_DeactivatesAgainstTheLoadedVersion()
    {
        Existing();

        var result = await _handler.HandleAsync(Command(ifMatch: null), Token);

        result.IsSuccess.ShouldBeTrue();
        await _repository.Received(1).SaveAsync(
            Arg.Any<Product>(), 1, Arg.Any<AuditStamp>(), Arg.Any<IReadOnlyCollection<PriceHistoryEntry>>(), Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_AlreadyInactive_ReturnsTheProductWithoutSaving()
    {
        var product = ProductTestData.Product("SKU-1");
        product.Deactivate();
        _repository.GetBySkuAsync(Sku.Create("SKU-1"), Token).Returns(product);

        var result = await _handler.HandleAsync(Command(ifMatch: null), Token);

        result.Value.IsActive.ShouldBeFalse();
        result.Value.Version.ShouldBe(2);
        await _repository.DidNotReceiveWithAnyArgs().SaveAsync(default!, default, default!, default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_UnknownSku_ReturnsNotFound()
    {
        var result = await _handler.HandleAsync(Command(), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_StaleIfMatch_ReturnsPreconditionFailed()
    {
        Existing();

        var result = await _handler.HandleAsync(Command(ifMatch: "\"9\""), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
        await _repository.DidNotReceiveWithAnyArgs().SaveAsync(default!, default, default!, default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_RepositoryReportsAConflict_ReturnsPreconditionFailed()
    {
        Existing();
        _repository
            .SaveAsync(Arg.Any<Product>(), Arg.Any<int?>(), Arg.Any<AuditStamp>(), Arg.Any<IReadOnlyCollection<PriceHistoryEntry>>(), Token)
            .Returns(Task.FromException(new ConcurrencyException("moved")));

        var result = await _handler.HandleAsync(Command(), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_InvalidSkuOrMalformedIfMatch_ReturnsValidationError()
    {
        Existing();

        (await _handler.HandleAsync(Command(sku: " "), Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await _handler.HandleAsync(Command(ifMatch: "abc"), Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    private void Existing() =>
        _repository.GetBySkuAsync(Sku.Create("SKU-1"), Token).Returns(ProductTestData.Product("SKU-1"));

    private static DeactivateProductCommand Command(string sku = "sku-1", string? ifMatch = "\"1\"") =>
        new(sku, ifMatch, "admin-1");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
