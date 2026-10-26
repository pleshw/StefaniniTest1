using System.Text.Json;
using NSubstitute.ExceptionExtensions;
using Orders.Application.Catalog;
using Orders.Application.Discounts;
using Orders.Application.Idempotency;
using Orders.Application.Ordering;
using Orders.Application.Pricing;
using Orders.Domain;
using Orders.Domain.Discounts;

namespace Orders.Application.UnitTests;

public sealed class CreateOrderHandlerTests
{
    private static readonly DateTimeOffset _now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly Guid _client = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly string _key = "22222222-2222-2222-2222-222222222222";
    private static readonly Currency _brl = Currency.Create("BRL");
    private static readonly byte[] _hash = [1, 2, 3];

    private readonly IIdempotencyStore _store = Substitute.For<IIdempotencyStore>();
    private readonly ICatalogPriceProvider _catalog = Substitute.For<ICatalogPriceProvider>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IOrderEventPublisher _events = Substitute.For<IOrderEventPublisher>();
    private readonly IOrderTransaction _transaction = Substitute.For<IOrderTransaction>();
    private readonly IActiveDiscountRules _rules = Substitute.For<IActiveDiscountRules>();
    private readonly IOrderQueries _queries = Substitute.For<IOrderQueries>();
    private readonly IOrderDiscountStore _discounts = Substitute.For<IOrderDiscountStore>();
    private readonly CreateOrderHandler _handler;

    public CreateOrderHandlerTests()
    {
        _store.TryReserveAsync(default, default, default!, default, Token).ReturnsForAnyArgs(true);
        _transaction.ExecuteAsync(Arg.Any<Func<CancellationToken, Task<Result<CreateOrderOutcome>>>>(), Token)
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<CreateOrderOutcome>>>>()(Token));
        _catalog.GetPricesAsync(_brl, Arg.Any<IReadOnlyCollection<string>>(), Token).Returns(Result.Ok(new CatalogPrices(
            [Price("KB-1", 19.99m, 3), Price("MS-1", 10m, 1)], [])));
        _rules.GetAsync(_brl, Token).Returns([]);
        var clock = new FixedTimeProvider(_now);
        _handler = new CreateOrderHandler(
            new IdempotencyService(_store),
            _store,
            new OrderDrafter(
                new PricingService(_catalog, new CartValidator(new CartLineValidator())), _rules, _queries, new DiscountEngine(clock), clock),
            _orders,
            _queries,
            _discounts,
            _transaction,
            _events,
            new CreateOrderCommandValidator());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_NewKey_PlacesThePricedOrderAndReturns201()
    {
        var result = await _handler.HandleAsync(Command(Line("KB-1", 2), Line("MS-1", 1)), Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(201);
        result.Value.Replayed.ShouldBeFalse();
        await _orders.Received(1).AddAsync(
            Arg.Is<Order>(order => order.Client.Id == _client
                && order.Client.Name == "Ana"
                && order.Channel == OrderChannel.Shop
                && order.CreatedBy == "shopper-1"
                && order.Status == OrderStatus.Pending
                && order.ItemCount == 3
                && order.Total.Amount == 49.98m
                && order.Number.StartsWith("ORD-2026-", StringComparison.Ordinal)),
            Token);
        using var body = JsonDocument.Parse(result.Value.Body);
        body.RootElement.GetProperty("total").GetDecimal().ShouldBe(49.98m);
        body.RootElement.GetProperty("status").GetString().ShouldBe("Pending");
        body.RootElement.GetProperty("items").GetArrayLength().ShouldBe(2);
        body.RootElement.GetProperty("id").GetGuid().ShouldBe(result.Value.OrderId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_NewKey_PublishesOrderPlacedAndStoresTheResponseInTheSameTransaction()
    {
        var result = await _handler.HandleAsync(Command(Line("KB-1", 1)), Token);

        await _transaction.Received(1).ExecuteAsync(Arg.Any<Func<CancellationToken, Task<Result<CreateOrderOutcome>>>>(), Token);
        await _events.Received(1).PublishAsync(Arg.Is<IDomainEvent>(domainEvent => domainEvent is OrderPlaced), Token);
        await _store.Received(1).TryReserveAsync(_client, Guid.Parse(_key), _hash, result.Value.OrderId, Token);
        await _store.Received(1).CompleteAsync(_client, Guid.Parse(_key), 201, result.Value.Body, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_AdminOnBehalfOfAClient_PlacesTheOrderForTheClientAndScopesTheKeyByIt()
    {
        var command = Command(Line("KB-1", 1)) with { CreatedBy = "admin-1", Channel = OrderChannel.Admin };

        var result = await _handler.HandleAsync(command, Token);

        result.IsSuccess.ShouldBeTrue();
        await _orders.Received(1).AddAsync(
            Arg.Is<Order>(order => order.Client.Id == _client && order.CreatedBy == "admin-1" && order.Channel == OrderChannel.Admin),
            Token);
        await _store.Received(1).TryGetAsync(_client, Guid.Parse(_key), Token);
        await _store.Received(1).TryReserveAsync(_client, Guid.Parse(_key), _hash, result.Value.OrderId, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_NewKey_CallsCatalogOnce()
    {
        await _handler.HandleAsync(Command(Line("KB-1", 1), Line("MS-1", 1)), Token);

        await _catalog.Received(1).GetPricesAsync(_brl, Arg.Any<IReadOnlyCollection<string>>(), Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_KeyAlreadyStoredWithTheSameHash_ReplaysWithoutPricingOrWriting()
    {
        _store.TryGetAsync(_client, Guid.Parse(_key), Token)
            .Returns(new IdempotencyRecord(_client, Guid.Parse(_key), _hash, 201, """{"id":"abc"}""", Guid.NewGuid()));

        var result = await _handler.HandleAsync(Command(Line("KB-1", 1)), Token);

        result.Value.Replayed.ShouldBeTrue();
        result.Value.Status.ShouldBe(201);
        result.Value.Body.ShouldBe("""{"id":"abc"}""");
        await _catalog.DidNotReceiveWithAnyArgs().GetPricesAsync(default!, default!, Token);
        await _transaction.DidNotReceiveWithAnyArgs().ExecuteAsync<Result<CreateOrderOutcome>>(default!, Token);
        await _orders.DidNotReceiveWithAnyArgs().AddAsync(default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_KeyAlreadyStoredWithAnotherHash_ReturnsUnprocessable()
    {
        _store.TryGetAsync(_client, Guid.Parse(_key), Token)
            .Returns(new IdempotencyRecord(_client, Guid.Parse(_key), [9, 9], 201, "{}", Guid.NewGuid()));

        var result = await _handler.HandleAsync(Command(Line("KB-1", 1)), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Unprocessable);
        await _orders.DidNotReceiveWithAnyArgs().AddAsync(default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ConcurrentRequestCommittedFirst_ReplaysItsResponse()
    {
        var winner = new IdempotencyRecord(_client, Guid.Parse(_key), _hash, 201, """{"id":"winner"}""", Guid.NewGuid());
        _store.TryGetAsync(_client, Guid.Parse(_key), Token).Returns(Task.FromResult<IdempotencyRecord?>(null), Task.FromResult<IdempotencyRecord?>(winner));
        _store.TryReserveAsync(default, default, default!, default, Token).ReturnsForAnyArgs(false);

        var result = await _handler.HandleAsync(Command(Line("KB-1", 1)), Token);

        result.Value.Replayed.ShouldBeTrue();
        result.Value.Body.ShouldBe("""{"id":"winner"}""");
        await _orders.DidNotReceiveWithAnyArgs().AddAsync(default!, Token);
        await _events.DidNotReceiveWithAnyArgs().PublishAsync(default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ConcurrentRequestWithAnotherBodyCommittedFirst_ReturnsUnprocessable()
    {
        var winner = new IdempotencyRecord(_client, Guid.Parse(_key), [7, 7], 201, "{}", Guid.NewGuid());
        _store.TryGetAsync(_client, Guid.Parse(_key), Token).Returns(Task.FromResult<IdempotencyRecord?>(null), Task.FromResult<IdempotencyRecord?>(winner));
        _store.TryReserveAsync(default, default, default!, default, Token).ReturnsForAnyArgs(false);

        var result = await _handler.HandleAsync(Command(Line("KB-1", 1)), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Unprocessable);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ReservationLostButNothingStored_Throws()
    {
        _store.TryReserveAsync(default, default, default!, default, Token).ReturnsForAnyArgs(false);

        await Should.ThrowAsync<InvalidOperationException>(() => _handler.HandleAsync(Command(Line("KB-1", 1)), Token));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    public async Task HandleAsync_MissingOrInvalidKey_ReturnsValidationErrorBeforeAnyWork(string? key)
    {
        var result = await _handler.HandleAsync(Command(Line("KB-1", 1)) with { IdempotencyKey = key }, Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _store.DidNotReceiveWithAnyArgs().TryGetAsync(default, default, Token);
        await _catalog.DidNotReceiveWithAnyArgs().GetPricesAsync(default!, default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_UnknownSku_ReturnsUnprocessableAndWritesNothing()
    {
        _catalog.GetPricesAsync(_brl, Arg.Any<IReadOnlyCollection<string>>(), Token)
            .Returns(Result.Ok(new CatalogPrices([], ["GONE-1"])));

        var result = await _handler.HandleAsync(Command(Line("GONE-1", 1)), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Unprocessable);
        result.Error.Message.ShouldContain("GONE-1");
        await _transaction.DidNotReceiveWithAnyArgs().ExecuteAsync<Result<CreateOrderOutcome>>(default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_CatalogUnavailable_ReturnsTheErrorAndWritesNothing()
    {
        _catalog.GetPricesAsync(_brl, Arg.Any<IReadOnlyCollection<string>>(), Token)
            .Returns(Result.Fail<CatalogPrices>(ErrorKind.CatalogUnavailable, "down"));

        var result = await _handler.HandleAsync(Command(Line("KB-1", 1)), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.CatalogUnavailable);
        await _store.DidNotReceiveWithAnyArgs().TryReserveAsync(default, default, default!, default, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_InvalidCartOrCurrencyOrClient_ReturnsValidationError()
    {
        (await _handler.HandleAsync(Command() with { Lines = [] }, Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await _handler.HandleAsync(Command(Line("KB-1", 0)), Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await _handler.HandleAsync(Command(Line("KB-1", 1)) with { Currency = "REAL" }, Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await _handler.HandleAsync(Command(Line("KB-1", 1)) with { ClientName = " " }, Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await _handler.HandleAsync(Command(Line("KB-1", 1)) with { ClientId = Guid.Empty }, Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _orders.DidNotReceiveWithAnyArgs().AddAsync(default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_NoCurrency_DefaultsToBrl()
    {
        var result = await _handler.HandleAsync(Command(Line("KB-1", 1)) with { Currency = null }, Token);

        result.IsSuccess.ShouldBeTrue();
        await _catalog.Received(1).GetPricesAsync(_brl, Arg.Any<IReadOnlyCollection<string>>(), Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ActiveRuleApplies_PlacesTheDiscountedOrderAndStoresTheDiscount()
    {
        var rule = Rule("""{ "percent": 10, "appliesTo": "order" }""", oncePerClient: true);
        _rules.GetAsync(_brl, Token).Returns([rule]);

        var result = await _handler.HandleAsync(Command(Line("KB-1", 2), Line("MS-1", 1)), Token);

        using var body = JsonDocument.Parse(result.Value.Body);
        body.RootElement.GetProperty("subtotal").GetDecimal().ShouldBe(49.98m);
        body.RootElement.GetProperty("discountTotal").GetDecimal().ShouldBe(5.00m);
        body.RootElement.GetProperty("total").GetDecimal().ShouldBe(44.98m);
        await _orders.Received(1).AddAsync(Arg.Is<Order>(order => order.DiscountTotal.Amount == 5.00m && order.Total.Amount == 44.98m), Token);
        await _discounts.Received(1).AddAsync(
            Arg.Is<Order>(order => order.Id == result.Value.OrderId),
            Arg.Is<IReadOnlyList<AppliedDiscount>>(applied => applied.Count == 1 && applied[0].RuleId == rule.Id
                && applied[0].RuleVersion == 3 && applied[0].Amount.Amount == 5.00m && applied[0].OncePerClient),
            Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_NoActiveRules_StoresNoDiscountAndKeepsFullPrices()
    {
        var result = await _handler.HandleAsync(Command(Line("KB-1", 1)), Token);

        using var body = JsonDocument.Parse(result.Value.Body);
        body.RootElement.GetProperty("discountTotal").GetDecimal().ShouldBe(0m);
        await _discounts.Received(1).AddAsync(Arg.Any<Order>(), Arg.Is<IReadOnlyList<AppliedDiscount>>(applied => applied.Count == 0), Token);
        await _queries.DidNotReceiveWithAnyArgs().ClientHasOrdersAsync(default, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_FirstOrderOfClientRule_AppliesOnlyWithoutEarlierOrders()
    {
        _rules.GetAsync(_brl, Token).Returns([Rule("""{ "percent": 10, "appliesTo": "order" }""", conditions: """[{ "kind": "FirstOrderOfClient" }]""")]);
        _queries.ClientHasOrdersAsync(_client, Token).Returns(false, false, true);

        var first = await _handler.HandleAsync(Command(Line("MS-1", 1)), Token);
        var second = await _handler.HandleAsync(Command(Line("MS-1", 1)) with { IdempotencyKey = Guid.NewGuid().ToString() }, Token);

        using var firstBody = JsonDocument.Parse(first.Value.Body);
        using var secondBody = JsonDocument.Parse(second.Value.Body);
        firstBody.RootElement.GetProperty("discountTotal").GetDecimal().ShouldBe(1m);
        secondBody.RootElement.GetProperty("discountTotal").GetDecimal().ShouldBe(0m);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_FirstOrderDraft_LocksTheClientAndChecksAgainBeforePlacing()
    {
        _rules.GetAsync(_brl, Token).Returns([Rule("""{ "percent": 10, "appliesTo": "order" }""", conditions: """[{ "kind": "FirstOrderOfClient" }]""")]);
        _queries.ClientHasOrdersAsync(_client, Token).Returns(false);

        var result = await _handler.HandleAsync(Command(Line("MS-1", 1)), Token);

        using var body = JsonDocument.Parse(result.Value.Body);
        body.RootElement.GetProperty("discountTotal").GetDecimal().ShouldBe(1m);
        await _orders.Received(1).LockClientAsync(_client, Token);
        await _queries.Received(2).ClientHasOrdersAsync(_client, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ClientOrderedWhileDrafting_DraftsAgainWithoutTheFirstOrderDiscount()
    {
        _rules.GetAsync(_brl, Token).Returns([Rule("""{ "percent": 10, "appliesTo": "order" }""", conditions: """[{ "kind": "FirstOrderOfClient" }]""")]);
        _queries.ClientHasOrdersAsync(_client, Token).Returns(false, true);

        var result = await _handler.HandleAsync(Command(Line("MS-1", 1)), Token);

        result.IsSuccess.ShouldBeTrue();
        using var body = JsonDocument.Parse(result.Value.Body);
        body.RootElement.GetProperty("discountTotal").GetDecimal().ShouldBe(0m);
        body.RootElement.GetProperty("total").GetDecimal().ShouldBe(10m);
        await _transaction.Received(2).ExecuteAsync(Arg.Any<Func<CancellationToken, Task<Result<CreateOrderOutcome>>>>(), Token);
        await _orders.Received(1).AddAsync(Arg.Is<Order>(order => order.DiscountTotal.Amount == 0m), Token);
        await _discounts.Received(1).AddAsync(Arg.Any<Order>(), Arg.Is<IReadOnlyList<AppliedDiscount>>(applied => applied.Count == 0), Token);
        await _events.Received(1).PublishAsync(Arg.Any<IDomainEvent>(), Token);
        await _orders.Received(1).LockClientAsync(_client, Token);
        await _catalog.Received(2).GetPricesAsync(_brl, Arg.Any<IReadOnlyCollection<string>>(), Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ClientWithEarlierOrders_TakesNoClientLock()
    {
        _rules.GetAsync(_brl, Token).Returns([Rule("""{ "percent": 10, "appliesTo": "order" }""", conditions: """[{ "kind": "FirstOrderOfClient" }]""")]);
        _queries.ClientHasOrdersAsync(_client, Token).Returns(true);

        await _handler.HandleAsync(Command(Line("MS-1", 1)), Token);

        await _orders.DidNotReceiveWithAnyArgs().LockClientAsync(default, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_NoActiveRules_TakesNoClientLock()
    {
        await _handler.HandleAsync(Command(Line("KB-1", 1)), Token);

        await _orders.DidNotReceiveWithAnyArgs().LockClientAsync(default, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_OncePerClientDiscountAlreadyRedeemed_ReturnsDiscountAlreadyUsedAndPublishesNothing()
    {
        _rules.GetAsync(_brl, Token).Returns([Rule("""{ "percent": 10, "appliesTo": "order" }""", oncePerClient: true)]);
        _discounts.AddAsync(default!, default!, Token)
            .ThrowsAsyncForAnyArgs(new DiscountAlreadyUsedException("used", new InvalidOperationException()));

        var result = await _handler.HandleAsync(Command(Line("KB-1", 1)), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.DiscountAlreadyUsed);
        await _events.DidNotReceiveWithAnyArgs().PublishAsync(default!, Token);
        await _store.DidNotReceiveWithAnyArgs().CompleteAsync(default, default, default, default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ReplayOfADiscountedOrder_NeverRecalculates()
    {
        _store.TryGetAsync(_client, Guid.Parse(_key), Token)
            .Returns(new IdempotencyRecord(_client, Guid.Parse(_key), _hash, 201, """{"total":44.98}""", Guid.NewGuid()));

        var result = await _handler.HandleAsync(Command(Line("KB-1", 1)), Token);

        result.Value.Body.ShouldBe("""{"total":44.98}""");
        await _rules.DidNotReceiveWithAnyArgs().GetAsync(default!, Token);
        await _discounts.DidNotReceiveWithAnyArgs().AddAsync(default!, default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OrderNumbers_For_UsesTheYearAndTwelveHexCharactersOfTheId()
    {
        var number = OrderNumbers.For(Guid.Parse("3f9a1c2b-7d40-4e8a-b1c2-9d8e7f6a5b4c"), _now);

        number.ShouldBe("ORD-2026-3F9A1C2B7D40");
    }

    private static CreateOrderCommand Command(params CartLine[] lines) => new(
        _client, "Ana", "shopper-1", OrderChannel.Shop, _key, "BRL", lines.Length == 0 ? [Line("KB-1", 1)] : lines, _hash);

    private static CartLine Line(string sku, int quantity) => new(sku, quantity);

    private static DiscountRule Rule(string parameters, bool oncePerClient = false, string conditions = "[]") =>
        DiscountRule.Create(Guid.NewGuid(), 3, "rule", DiscountType.Percentage, parameters, conditions, 1, false, oncePerClient, _now.AddDays(-1), null, true);

    private static CatalogPrice Price(string sku, decimal amount, int version) =>
        new(sku, $"Name {sku}", "peripherals", Money.Create(amount, _brl), version, true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
