using NSubstitute.ExceptionExtensions;
using Orders.Application.Ordering;
using Orders.Domain;

namespace Orders.Application.UnitTests;

public sealed class ChangeOrderStatusHandlerTests
{
    private static readonly DateTimeOffset _placed = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly DateTimeOffset _now = new(2026, 3, 4, 6, 0, 0, TimeSpan.Zero);
    private static readonly Currency _brl = Currency.Create("BRL");

    // Pending, OnHold and Confirmed are the only statuses an admin can move an order out of.
    public static TheoryData<OrderStatus, OrderAction, bool> Transitions => new()
    {
        { OrderStatus.Pending, OrderAction.Confirm, true },
        { OrderStatus.Pending, OrderAction.Hold, true },
        { OrderStatus.Pending, OrderAction.Block, true },
        { OrderStatus.Pending, OrderAction.Cancel, false },
        { OrderStatus.OnHold, OrderAction.Confirm, true },
        { OrderStatus.OnHold, OrderAction.Hold, false },
        { OrderStatus.OnHold, OrderAction.Block, true },
        { OrderStatus.OnHold, OrderAction.Cancel, false },
        { OrderStatus.Confirmed, OrderAction.Confirm, false },
        { OrderStatus.Confirmed, OrderAction.Hold, false },
        { OrderStatus.Confirmed, OrderAction.Block, false },
        { OrderStatus.Confirmed, OrderAction.Cancel, true },
        { OrderStatus.Blocked, OrderAction.Confirm, false },
        { OrderStatus.Blocked, OrderAction.Hold, false },
        { OrderStatus.Blocked, OrderAction.Block, false },
        { OrderStatus.Blocked, OrderAction.Cancel, false },
        { OrderStatus.Cancelled, OrderAction.Confirm, false },
        { OrderStatus.Cancelled, OrderAction.Hold, false },
        { OrderStatus.Cancelled, OrderAction.Block, false },
        { OrderStatus.Cancelled, OrderAction.Cancel, false },
        { OrderStatus.Failed, OrderAction.Confirm, false },
        { OrderStatus.Failed, OrderAction.Hold, false },
        { OrderStatus.Failed, OrderAction.Block, false },
        { OrderStatus.Failed, OrderAction.Cancel, false },
    };

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IOrderEventLog _log = Substitute.For<IOrderEventLog>();
    private readonly IOrderEventPublisher _events = Substitute.For<IOrderEventPublisher>();
    private readonly IOrderTransaction _transaction = Substitute.For<IOrderTransaction>();
    private readonly ChangeOrderStatusHandler _handler;

    public ChangeOrderStatusHandlerTests()
    {
        _transaction.ExecuteAsync(Arg.Any<Func<CancellationToken, Task<Result<OrderDto>>>>(), Token)
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<OrderDto>>>>()(Token));
        _handler = new ChangeOrderStatusHandler(
            _orders, _transaction, new OrderStatusChangeRecorder(_log, _events), new FixedTimeProvider(_now), new ChangeOrderStatusCommandValidator());
    }

    [Theory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(Transitions))]
    public async Task HandleAsync_EachStatusAndAction_FollowsTheStateMachine(OrderStatus from, OrderAction action, bool allowed)
    {
        var order = OrderIn(from);
        Load(order);

        var result = await _handler.HandleAsync(Command(order, action), Token);

        if (allowed)
        {
            result.IsSuccess.ShouldBeTrue();
            result.Value.Status.ShouldBe(Target(action).ToString());
            order.Status.ShouldBe(Target(action));
            await _orders.Received(1).UpdateAsync(order, order.RowVersion - 1, Token);
        }
        else
        {
            result.Error!.Kind.ShouldBe(ErrorKind.InvalidTransition);
            order.Status.ShouldBe(from);
            await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default, Token);
            await _events.DidNotReceiveWithAnyArgs().PublishAsync(default!, Token);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_Hold_RecordsTheAdminAsActorWithTheReasonAndTheNewRowVersion()
    {
        var order = OrderIn(OrderStatus.Pending);
        Load(order);

        var result = await _handler.HandleAsync(Command(order, OrderAction.Hold, reason: "Call the customer"), Token);

        var change = order.StatusHistory[^1];
        (change.From, change.To, change.Reason, change.Actor, change.ChangedAt).ShouldBe((OrderStatus.Pending, OrderStatus.OnHold, "Call the customer", "admin-1", _now));
        result.Value.RowVersion.ShouldBe(1);
        await _orders.Received(1).UpdateAsync(order, 0, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_Transition_WritesTheEventLogAndPublishesOrderStatusChangedInTheSameTransaction()
    {
        var order = OrderIn(OrderStatus.Pending);
        Load(order);

        await _handler.HandleAsync(Command(order, OrderAction.Block, reason: "Fraud suspected"), Token);

        await _transaction.Received(1).ExecuteAsync(Arg.Any<Func<CancellationToken, Task<Result<OrderDto>>>>(), Token);
        await _log.Received(1).AppendAsync(
            Arg.Is<IDomainEvent>(domainEvent => domainEvent is OrderStatusChanged
                && ((OrderStatusChanged)domainEvent).To == OrderStatus.Blocked
                && ((OrderStatusChanged)domainEvent).Actor == "admin-1"
                && ((OrderStatusChanged)domainEvent).Reason == "Fraud suspected"),
            Token);
        await _events.Received(1).PublishAsync(Arg.Is<IDomainEvent>(domainEvent => domainEvent is OrderStatusChanged), Token);
        order.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_ConfirmWithoutReason_IsFine()
    {
        var order = OrderIn(OrderStatus.OnHold);
        Load(order);

        var result = await _handler.HandleAsync(Command(order, OrderAction.Confirm), Token);

        result.IsSuccess.ShouldBeTrue();
        order.StatusHistory[^1].Reason.ShouldBeNull();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(OrderAction.Hold, null)]
    [InlineData(OrderAction.Hold, "")]
    [InlineData(OrderAction.Block, "   ")]
    [InlineData(OrderAction.Cancel, null)]
    public async Task HandleAsync_HoldBlockOrCancelWithoutAReason_IsAValidationError(OrderAction action, string? reason)
    {
        var order = OrderIn(OrderStatus.Confirmed);
        Load(order);

        var result = await _handler.HandleAsync(Command(order, action, reason: reason), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _orders.DidNotReceiveWithAnyArgs().GetByIdAsync(default, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_VeryLongReason_IsAValidationError()
    {
        var order = OrderIn(OrderStatus.Pending);

        var result = await _handler.HandleAsync(Command(order, OrderAction.Block, reason: new string('x', 501)), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_StaleRowVersion_IsPreconditionFailedAndChangesNothing()
    {
        var order = OrderIn(OrderStatus.Pending);
        Load(order);

        var result = await _handler.HandleAsync(Command(order, OrderAction.Confirm) with { IfMatch = "\"5\"" }, Token);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
        order.Status.ShouldBe(OrderStatus.Pending);
        await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_WithoutIfMatch_IsPreconditionRequired()
    {
        var order = OrderIn(OrderStatus.Pending);
        Load(order);

        var result = await _handler.HandleAsync(Command(order, OrderAction.Confirm) with { IfMatch = null }, Token);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionRequired);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_MalformedIfMatch_IsAValidationError()
    {
        var order = OrderIn(OrderStatus.Pending);

        var result = await _handler.HandleAsync(Command(order, OrderAction.Confirm) with { IfMatch = "zero" }, Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_UnknownOrder_IsNotFound()
    {
        _orders.GetByIdAsync(Arg.Any<Guid>(), Token).Returns((Order?)null);

        var result = await _handler.HandleAsync(new ChangeOrderStatusCommand(Guid.NewGuid(), OrderAction.Confirm, "\"0\"", null, "admin-1"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_SomeoneChangedTheOrderBetweenReadAndWrite_IsPreconditionFailed()
    {
        var order = OrderIn(OrderStatus.Pending);
        Load(order);
        _orders.UpdateAsync(default!, default, Token).ThrowsAsyncForAnyArgs(new ConcurrencyException("changed"));

        var result = await _handler.HandleAsync(Command(order, OrderAction.Confirm), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
        await _events.DidNotReceiveWithAnyArgs().PublishAsync(default!, Token);
    }

    private static ChangeOrderStatusCommand Command(Order order, OrderAction action, string? reason = "Because") =>
        new(order.Id, action, $"\"{order.RowVersion}\"", action == OrderAction.Confirm && reason == "Because" ? null : reason, "admin-1");

    private static OrderStatus Target(OrderAction action) => action switch
    {
        OrderAction.Confirm => OrderStatus.Confirmed,
        OrderAction.Hold => OrderStatus.OnHold,
        OrderAction.Block => OrderStatus.Blocked,
        _ => OrderStatus.Cancelled,
    };

    /// <summary>A stored order in the given status, read back without unsent events, with its row version.</summary>
    private static Order OrderIn(OrderStatus status)
    {
        var order = Order.Place(
            Guid.NewGuid(), "ORD-2026-0", ClientRef.Create(Guid.NewGuid(), "Ana"), "shopper-1", OrderChannel.Shop,
            [OrderItem.Create("KB-1", "Keyboard", Money.Create(10m, _brl), 1, 1)], _placed);

        switch (status)
        {
            case OrderStatus.OnHold:
                order.Hold("system:worker", "rule", _placed);
                break;
            case OrderStatus.Confirmed:
                order.Confirm("system:worker", _placed);
                break;
            case OrderStatus.Blocked:
                order.Block("admin-0", "fraud", _placed);
                break;
            case OrderStatus.Cancelled:
                order.Confirm("system:worker", _placed);
                order.Cancel("admin-0", "customer asked", _placed);
                break;
            case OrderStatus.Failed:
                order.Fail("system:worker", "payment failed", _placed);
                break;
        }

        order.ClearDomainEvents();

        return order;
    }

    private void Load(Order order) => _orders.GetByIdAsync(order.Id, Token).Returns(order);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
