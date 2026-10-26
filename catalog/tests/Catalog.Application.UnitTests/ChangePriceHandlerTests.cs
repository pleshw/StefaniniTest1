using Catalog.Application.Products;
using Catalog.Domain;

namespace Catalog.Application.UnitTests;

public sealed class ChangePriceHandlerTests
{
    private static readonly DateTimeOffset _now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly ChangePriceHandler _handler;

    public ChangePriceHandlerTests()
    {
        _handler = new ChangePriceHandler(
            _repository,
            ProductTestData.Currencies,
            new FixedTimeProvider(_now),
            new ChangePriceCommandValidator(ProductTestData.Currencies));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_MatchingIfMatch_SavesTheNewPriceWithOneHistoryRow()
    {
        Existing();

        var result = await _handler.HandleAsync(Command(), Token);

        result.Value.Version.ShouldBe(2);
        result.Value.Prices.Single(price => price.Currency == "BRL").Amount.ShouldBe(120m);
        await _repository.Received(1).SaveAsync(
            Arg.Is<Product>(product => product.Version == 2),
            1,
            Arg.Is<AuditStamp>(audit => audit.UpdatedBy == "admin-1" && audit.UpdatedAt == _now),
            Arg.Is<IReadOnlyCollection<PriceHistoryEntry>>(history => IsBrlChange(history)),
            Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_CurrencyWithoutPrice_AddsItWithAnEmptyOldPrice()
    {
        _repository.GetBySkuAsync(Sku.Create("SKU-1"), Token)
            .Returns(ProductTestData.Product("SKU-1", "Keyboard", ProductTestData.Brl(100m)));

        var result = await _handler.HandleAsync(Command(currency: "USD", amount: 20m), Token);

        result.Value.Prices.Count.ShouldBe(2);
        await _repository.Received(1).SaveAsync(
            Arg.Any<Product>(),
            1,
            Arg.Any<AuditStamp>(),
            Arg.Is<IReadOnlyCollection<PriceHistoryEntry>>(history => history.Single().OldPrice == null),
            Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_SamePrice_ReturnsTheProductWithoutSaving()
    {
        Existing();

        var result = await _handler.HandleAsync(Command(amount: 100m), Token);

        result.Value.Version.ShouldBe(1);
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
    public async Task HandleAsync_MissingIfMatch_ReturnsPreconditionRequired()
    {
        Existing();

        var result = await _handler.HandleAsync(Command(ifMatch: null), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionRequired);
        await _repository.DidNotReceiveWithAnyArgs().SaveAsync(default!, default, default!, default!, Token);
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
    public async Task HandleAsync_MalformedIfMatch_ReturnsValidationError()
    {
        Existing();

        await AssertInvalid(Command(ifMatch: "abc"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_InvalidInput_ReturnsValidationError()
    {
        Existing();

        await AssertInvalid(Command(sku: " "));
        await AssertInvalid(Command(currency: "EUR"));
        await AssertInvalid(Command(currency: null));
        await AssertInvalid(Command(amount: null));
        await AssertInvalid(Command(amount: -1m));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_DeactivatedProduct_ReturnsValidationError()
    {
        var product = ProductTestData.Product("SKU-1");
        product.Deactivate();
        _repository.GetBySkuAsync(Sku.Create("SKU-1"), Token).Returns(product);

        await AssertInvalid(Command(ifMatch: "\"2\""));
    }

    private async Task AssertInvalid(ChangePriceCommand command)
    {
        var result = await _handler.HandleAsync(command, Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _repository.DidNotReceiveWithAnyArgs().SaveAsync(default!, default, default!, default!, Token);
    }

    private void Existing() =>
        _repository.GetBySkuAsync(Sku.Create("SKU-1"), Token).Returns(ProductTestData.Product("SKU-1"));

    private static bool IsBrlChange(IReadOnlyCollection<PriceHistoryEntry> history)
    {
        var entry = history.Single();
        return entry.Sku.Value == "SKU-1"
            && entry.Currency.Code == "BRL"
            && entry.OldPrice == 100m
            && entry.NewPrice == 120m
            && entry.Version == 2
            && entry.ChangedBy == "admin-1"
            && entry.ChangedAt == _now;
    }

    private static ChangePriceCommand Command(
        string sku = "sku-1",
        string? ifMatch = "\"1\"",
        string? currency = "BRL",
        decimal? amount = 120m) =>
        new(sku, ifMatch, currency, amount, "admin-1");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
