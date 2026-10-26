using Catalog.Application.Products;
using Catalog.Domain;

namespace Catalog.Application.UnitTests;

public sealed class UpsertProductHandlerTests
{
    private static readonly DateTimeOffset _now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly UpsertProductHandler _handler;

    public UpsertProductHandlerTests()
    {
        _handler = new UpsertProductHandler(_repository, ProductTestData.Currencies, new FixedTimeProvider(_now));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_AbsentProduct_CreatesItAndStampsTheAdmin()
    {
        var result = await _handler.HandleAsync(Command(), Token);

        result.Value.Created.ShouldBeTrue();
        result.Value.Product.Sku.ShouldBe("SKU-1");
        result.Value.Product.Version.ShouldBe(1);
        await _repository.Received(1).SaveAsync(
            Arg.Is<Product>(product => product.Sku.Value == "SKU-1"),
            null,
            Arg.Is<AuditStamp>(audit => audit.UpdatedBy == "admin-1" && audit.UpdatedAt == _now),
            Arg.Is<IReadOnlyCollection<PriceHistoryEntry>>(history => history.Count == 0),
            Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ExistingProductWithMatchingIfMatch_ReplacesItWithPriceHistory()
    {
        Existing();

        var result = await _handler.HandleAsync(
            Command(ifMatch: "\"1\"", prices: [new PriceInput("BRL", 120m), new PriceInput("USD", 20m)]), Token);

        result.Value.Created.ShouldBeFalse();
        result.Value.Product.Version.ShouldBe(2);
        await _repository.Received(1).SaveAsync(
            Arg.Any<Product>(),
            1,
            Arg.Is<AuditStamp>(audit => audit.UpdatedBy == "admin-1"),
            Arg.Is<IReadOnlyCollection<PriceHistoryEntry>>(history => HasSingleBrlChange(history)),
            Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ExistingProductWithoutIfMatch_ReturnsPreconditionRequired()
    {
        Existing();

        var result = await _handler.HandleAsync(Command(), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionRequired);
        await _repository.DidNotReceiveWithAnyArgs().SaveAsync(default!, default, default!, default!, Token);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("\"7\"")]
    [InlineData("W/\"7\"")]
    public async Task HandleAsync_ExistingProductWithStaleIfMatch_ReturnsPreconditionFailed(string ifMatch)
    {
        Existing();

        var result = await _handler.HandleAsync(Command(ifMatch: ifMatch), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
        await _repository.DidNotReceiveWithAnyArgs().SaveAsync(default!, default, default!, default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_AbsentProductWithIfMatch_ReturnsPreconditionFailed()
    {
        var result = await _handler.HandleAsync(Command(ifMatch: "\"1\""), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ExistingProductWithIdenticalContent_ReturnsItWithoutSaving()
    {
        Existing();

        var result = await _handler.HandleAsync(Command(ifMatch: "\"1\""), Token);

        result.Value.Created.ShouldBeFalse();
        result.Value.Product.Version.ShouldBe(1);
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

        var result = await _handler.HandleAsync(Command(ifMatch: "\"1\"", name: "Other"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_InvalidSku_ReturnsValidationError()
    {
        await AssertInvalid(Command(sku: " "));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_BlankNameOrCategory_ReturnsValidationError()
    {
        await AssertInvalid(Command(name: " "));
        await AssertInvalid(Command(category: ""));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_BadPrices_ReturnsValidationError()
    {
        await AssertInvalid(Command(prices: [new PriceInput("EUR", 1m)]));
        await AssertInvalid(Command(prices: [new PriceInput("BRL", 1m), new PriceInput("brl", 2m)]));
        await AssertInvalid(Command(prices: [new PriceInput("BRL", -1m)]));
        await AssertInvalid(Command(prices: [new PriceInput("BRL", null)]));
        await AssertInvalid(Command() with { Prices = null });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_MalformedIfMatch_ReturnsValidationError()
    {
        Existing();

        await AssertInvalid(Command(ifMatch: "abc"));
    }

    private async Task AssertInvalid(UpsertProductCommand command)
    {
        var result = await _handler.HandleAsync(command, Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _repository.DidNotReceiveWithAnyArgs().SaveAsync(default!, default, default!, default!, Token);
    }

    private void Existing() =>
        _repository.GetBySkuAsync(Sku.Create("SKU-1"), Token).Returns(ProductTestData.Product("SKU-1", "Keyboard"));

    private static bool HasSingleBrlChange(IReadOnlyCollection<PriceHistoryEntry> history)
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

    private static UpsertProductCommand Command(
        string sku = "sku-1",
        string? ifMatch = null,
        string? name = "Keyboard",
        string? category = "peripherals",
        IReadOnlyList<PriceInput>? prices = null) =>
        new(
            sku,
            ifMatch,
            name,
            "A keyboard",
            category,
            new Dictionary<string, string> { ["layout"] = "abnt2" },
            prices ?? [new PriceInput("BRL", 100m), new PriceInput("USD", 20m)],
            "admin-1");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
