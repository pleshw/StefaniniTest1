using NSubstitute.ExceptionExtensions;
using Orders.Application.Confirmation;
using Orders.Application.Ordering;
using Orders.Domain;
using Orders.Domain.Confirmation;

namespace Orders.Application.UnitTests;

public sealed class EvaluateConfirmationHandlerTests
{
    private const string _largeBrl = """[{ "kind": "TotalAbove", "amount": 5000, "currency": "BRL" }]""";
    private const string _firstOrder = """[{ "kind": "FirstOrderOfClient" }]""";

    private static readonly DateTimeOffset _placed = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly DateTimeOffset _now = new(2026, 3, 4, 5, 6, 37, TimeSpan.Zero);
    private static readonly Currency _brl = Currency.Create("BRL");
    private static readonly Guid _client = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IOrderQueries _queries = Substitute.For<IOrderQueries>();
    private readonly IActiveConfirmationRules _rules = Substitute.For<IActiveConfirmationRules>();
    private readonly IOrderEventLog _log = Substitute.For<IOrderEventLog>();
    private readonly IOrderEventPublisher _events = Substitute.For<IOrderEventPublisher>();
    private readonly IOrderTransaction _transaction = Substitute.For<IOrderTransaction>();
    private readonly EvaluateConfirmationHandler _handler;

    public EvaluateConfirmationHandlerTests()
    {
        _transaction.ExecuteAsync(Arg.Any<Func<CancellationToken, Task<Result<ConfirmationOutcome>>>>(), Token)
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<ConfirmationOutcome>>>>()(Token));
        _rules.GetAsync(Token).Returns([]);
        _handler = new EvaluateConfirmationHandler(
            _orders, _queries, _rules, _transaction, new OrderStatusChangeRecorder(_log, _events), new FixedTimeProvider(_now), new EvaluateConfirmationCommandValidator());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_HoldRuleMatches_HoldsTheOrderWithTheRuleNameAsReason()
    {
        var order = PendingOrder(unitPrice: 6000m);
        Load(order);
        _rules.GetAsync(Token).Returns([Rule("Large BRL order", _largeBrl)]);

        var result = await _handler.HandleAsync(new EvaluateConfirmationCommand(order.Id), Token);

        result.Value.ShouldBe(ConfirmationOutcome.Held);
        order.Status.ShouldBe(OrderStatus.OnHold);
        var change = order.StatusHistory[^1];
        change.Reason.ShouldBe("Large BRL order");
        change.Actor.ShouldBe("system:worker");
        change.ChangedAt.ShouldBe(_now);
        await _orders.Received(1).UpdateAsync(order, 0, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_NoRuleMatches_ConfirmsTheOrderAsTheWorker()
    {
        var order = PendingOrder(unitPrice: 10m);
        Load(order);
        _rules.GetAsync(Token).Returns([Rule("Large BRL order", _largeBrl)]);

        var result = await _handler.HandleAsync(new EvaluateConfirmationCommand(order.Id), Token);

        result.Value.ShouldBe(ConfirmationOutcome.Confirmed);
        order.Status.ShouldBe(OrderStatus.Confirmed);
        order.StatusHistory[^1].Actor.ShouldBe("system:worker");
        order.StatusHistory[^1].Reason.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_NoRulesAtAll_Confirms()
    {
        var order = PendingOrder(unitPrice: 99999m);
        Load(order);

        var result = await _handler.HandleAsync(new EvaluateConfirmationCommand(order.Id), Token);

        result.Value.ShouldBe(ConfirmationOutcome.Confirmed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_Transition_WritesTheEventLogAndPublishesOrderStatusChangedInTheSameTransaction()
    {
        var order = PendingOrder(unitPrice: 6000m);
        Load(order);
        _rules.GetAsync(Token).Returns([Rule("Large BRL order", _largeBrl)]);

        await _handler.HandleAsync(new EvaluateConfirmationCommand(order.Id), Token);

        await _transaction.Received(1).ExecuteAsync(Arg.Any<Func<CancellationToken, Task<Result<ConfirmationOutcome>>>>(), Token);
        await _log.Received(1).AppendAsync(
            Arg.Is<IDomainEvent>(domainEvent => domainEvent is OrderStatusChanged
                && ((OrderStatusChanged)domainEvent).From == OrderStatus.Pending
                && ((OrderStatusChanged)domainEvent).To == OrderStatus.OnHold
                && ((OrderStatusChanged)domainEvent).Actor == "system:worker"),
            Token);
        await _events.Received(1).PublishAsync(Arg.Is<IDomainEvent>(domainEvent => domainEvent is OrderStatusChanged), Token);
        order.DomainEvents.ShouldBeEmpty();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.OnHold)]
    [InlineData(OrderStatus.Blocked)]
    [InlineData(OrderStatus.Cancelled)]
    public async Task HandleAsync_OrderNoLongerPending_DoesNothing(OrderStatus status)
    {
        var order = PendingOrder(unitPrice: 6000m);
        Move(order, status);
        Load(order);
        _rules.GetAsync(Token).Returns([Rule("Large BRL order", _largeBrl)]);

        var result = await _handler.HandleAsync(new EvaluateConfirmationCommand(order.Id), Token);

        result.Value.ShouldBe(ConfirmationOutcome.NotPending);
        order.Status.ShouldBe(status);
        await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default, Token);
        await _log.DidNotReceiveWithAnyArgs().AppendAsync(default!, Token);
        await _events.DidNotReceiveWithAnyArgs().PublishAsync(default!, Token);
        await _transaction.DidNotReceiveWithAnyArgs().ExecuteAsync<Result<ConfirmationOutcome>>(default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_SameEventTwice_TransitionsOnlyOnce()
    {
        var order = PendingOrder(unitPrice: 6000m);
        Load(order);
        _rules.GetAsync(Token).Returns([Rule("Large BRL order", _largeBrl)]);

        var first = await _handler.HandleAsync(new EvaluateConfirmationCommand(order.Id), Token);
        var second = await _handler.HandleAsync(new EvaluateConfirmationCommand(order.Id), Token);

        first.Value.ShouldBe(ConfirmationOutcome.Held);
        second.Value.ShouldBe(ConfirmationOutcome.NotPending);
        order.StatusHistory.Count.ShouldBe(2);
        await _orders.Received(1).UpdateAsync(Arg.Any<Order>(), Arg.Any<int>(), Token);
        await _events.Received(1).PublishAsync(Arg.Any<IDomainEvent>(), Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_UnknownOrder_IsNotFound()
    {
        _orders.GetByIdAsync(Arg.Any<Guid>(), Token).Returns((Order?)null);

        var result = await _handler.HandleAsync(new EvaluateConfirmationCommand(Guid.NewGuid()), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_EmptyOrderId_IsAValidationError()
    {
        var result = await _handler.HandleAsync(new EvaluateConfirmationCommand(Guid.Empty), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_AdminActedBetweenReadAndWrite_ReadsAgainAndDoesNothing()
    {
        var stale = PendingOrder(unitPrice: 6000m);
        var current = Copy(stale);
        current.Confirm("admin-1", _now);
        _orders.GetByIdAsync(stale.Id, Token).Returns(stale, current);
        _orders.UpdateAsync(default!, default, Token).ThrowsAsyncForAnyArgs(new ConcurrencyException("changed"));

        var result = await _handler.HandleAsync(new EvaluateConfirmationCommand(stale.Id), Token);

        result.Value.ShouldBe(ConfirmationOutcome.NotPending);
        await _orders.Received(1).UpdateAsync(Arg.Any<Order>(), Arg.Any<int>(), Token);
        await _events.DidNotReceiveWithAnyArgs().PublishAsync(default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ConflictThenStillPending_TriesAgainAndTransitions()
    {
        var first = PendingOrder(unitPrice: 10m);
        var second = Copy(first);
        _orders.GetByIdAsync(first.Id, Token).Returns(first, second);
        var calls = 0;
        _orders.UpdateAsync(default!, default, Token).ReturnsForAnyArgs(_ => ++calls == 1 ? throw new ConcurrencyException("changed") : Task.CompletedTask);

        var result = await _handler.HandleAsync(new EvaluateConfirmationCommand(first.Id), Token);

        result.Value.ShouldBe(ConfirmationOutcome.Confirmed);
        second.Status.ShouldBe(OrderStatus.Confirmed);
        await _events.Received(1).PublishAsync(Arg.Any<IDomainEvent>(), Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ConflictEveryTime_ThrowsSoTheMessageIsRetried()
    {
        var order = PendingOrder(unitPrice: 10m);
        _orders.GetByIdAsync(order.Id, Token).Returns(_ => Copy(order));
        _orders.UpdateAsync(default!, default, Token).ThrowsAsyncForAnyArgs(new ConcurrencyException("changed"));

        await Should.ThrowAsync<ConcurrencyException>(() => _handler.HandleAsync(new EvaluateConfirmationCommand(order.Id), Token));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(false, ConfirmationOutcome.Held)]
    [InlineData(true, ConfirmationOutcome.Confirmed)]
    public async Task HandleAsync_FirstOrderRule_LooksForEarlierOrdersOfTheClientOnly(bool hasEarlier, ConfirmationOutcome expected)
    {
        var order = PendingOrder(unitPrice: 10m);
        Load(order);
        _rules.GetAsync(Token).Returns([Rule("First order review", _firstOrder)]);
        _queries.ClientHasEarlierOrdersAsync(_client, order.Id, order.CreatedAt, Token).Returns(hasEarlier);

        var result = await _handler.HandleAsync(new EvaluateConfirmationCommand(order.Id), Token);

        result.Value.ShouldBe(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_TotalAbove_ComparesTheTotalAfterDiscounts()
    {
        var order = Placed(OrderItem.Create("KB-1", "Keyboard", Money.Create(3000m, _brl), 1, 2, Money.Create(1500m, _brl)));
        Load(order);
        _rules.GetAsync(Token).Returns([Rule("Large BRL order", _largeBrl)]);

        var result = await _handler.HandleAsync(new EvaluateConfirmationCommand(order.Id), Token);

        order.Total.Amount.ShouldBe(4500m);
        result.Value.ShouldBe(ConfirmationOutcome.Confirmed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_RuleOnlyForAnotherCurrency_DoesNotHold()
    {
        var usd = Currency.Create("USD");
        var order = Placed(OrderItem.Create("KB-1", "Keyboard", Money.Create(9000m, usd), 1, 1));
        Load(order);
        _rules.GetAsync(Token).Returns([Rule("Large BRL order", _largeBrl)]);

        var result = await _handler.HandleAsync(new EvaluateConfirmationCommand(order.Id), Token);

        result.Value.ShouldBe(ConfirmationOutcome.Confirmed);
    }

    private static ConfirmationRule Rule(string name, string conditions) =>
        ConfirmationRule.Create(Guid.NewGuid(), 1, name, ConfirmationAction.Hold, conditions, 1, true);

    private static Order PendingOrder(decimal unitPrice) => Placed(OrderItem.Create("KB-1", "Keyboard", Money.Create(unitPrice, _brl), 1, 1));

    /// <summary>A stored order as the Worker reads it: pending and without the OrderPlaced event the API raised.</summary>
    private static Order Placed(params OrderItem[] items)
    {
        var order = Order.Place(Guid.NewGuid(), "ORD-2026-0", ClientRef.Create(_client, "Ana"), "shopper-1", OrderChannel.Shop, items, _placed);
        order.ClearDomainEvents();

        return order;
    }

    /// <summary>The same stored order read again: pending, version 0, no unsent events.</summary>
    private static Order Copy(Order order) => Order.Restore(
        order.Id, order.Number, order.Client, order.CreatedBy, order.Channel, OrderStatus.Pending, order.Items, order.StatusHistory,
        order.CreatedAt, order.UpdatedAt, 0);

    private static void Move(Order order, OrderStatus status)
    {
        switch (status)
        {
            case OrderStatus.Confirmed:
                order.Confirm("admin-1", _now);
                break;
            case OrderStatus.OnHold:
                order.Hold("admin-1", "check", _now);
                break;
            case OrderStatus.Blocked:
                order.Block("admin-1", "fraud", _now);
                break;
            case OrderStatus.Cancelled:
                order.Confirm("admin-1", _now);
                order.Cancel("admin-1", "customer asked", _now);
                break;
        }

        order.ClearDomainEvents();
    }

    private void Load(Order order) => _orders.GetByIdAsync(order.Id, Token).Returns(order);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
