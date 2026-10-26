namespace Orders.Domain.UnitTests;

public sealed class OrderStatusTransitionTests
{
    private static readonly DateTimeOffset _later = OrderBuilder.Now.AddMinutes(5);

    private static readonly (OrderStatus From, OrderStatus To)[] _allowed =
    [
        (OrderStatus.Pending, OrderStatus.Confirmed),
        (OrderStatus.Pending, OrderStatus.OnHold),
        (OrderStatus.Pending, OrderStatus.Blocked),
        (OrderStatus.Pending, OrderStatus.Failed),
        (OrderStatus.OnHold, OrderStatus.Confirmed),
        (OrderStatus.OnHold, OrderStatus.Blocked),
        (OrderStatus.Confirmed, OrderStatus.Cancelled),
    ];

    public static TheoryData<OrderStatus, OrderStatus> AllowedTransitions() => ToTheoryData(_allowed);

    public static TheoryData<OrderStatus, OrderStatus> ForbiddenTransitions() => ToTheoryData(
        Enum.GetValues<OrderStatus>()
            .SelectMany(from => Enum.GetValues<OrderStatus>().Select(to => (from, to)))
            .Where(pair => pair.to != OrderStatus.Pending) // no method returns an order to Pending: unrepresentable
            .Where(pair => !_allowed.Contains(pair)));

    [Theory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(AllowedTransitions))]
    public void TransitionTo_AllowedPair_ChangesStatusAndRecordsActorReasonAndEvent(OrderStatus from, OrderStatus to)
    {
        var order = OrderBuilder.InStatus(from);
        order.ClearDomainEvents();
        var historyBefore = order.StatusHistory.Count;
        var versionBefore = order.RowVersion;

        Apply(order, to, "admin-1", "because");

        order.Status.ShouldBe(to);
        order.UpdatedAt.ShouldBe(_later);
        order.RowVersion.ShouldBe(versionBefore + 1);
        order.StatusHistory.Count.ShouldBe(historyBefore + 1);
        order.StatusHistory[^1].ShouldBe(new OrderStatusChange(from, to, "because", "admin-1", _later));
        order.DomainEvents.ShouldHaveSingleItem().ShouldBe(new OrderStatusChanged(order.Id, from, to, "because", "admin-1", _later));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(ForbiddenTransitions))]
    public void TransitionTo_ForbiddenPair_ThrowsAndChangesNothing(OrderStatus from, OrderStatus to)
    {
        var order = OrderBuilder.InStatus(from);
        order.ClearDomainEvents();
        var historyBefore = order.StatusHistory.Count;

        var exception = Should.Throw<InvalidStatusTransitionException>(() => Apply(order, to, "admin-1", "because"));

        exception.From.ShouldBe(from);
        exception.To.ShouldBe(to);
        order.Status.ShouldBe(from);
        order.StatusHistory.Count.ShouldBe(historyBefore);
        order.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Confirm_NoReason_IsAllowed()
    {
        var order = OrderBuilder.Pending();

        order.Confirm("system:worker", _later);

        order.StatusHistory[^1].Reason.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Transition_BlankReasonWhereRequired_ThrowsAndChangesNothing()
    {
        var order = OrderBuilder.Pending();

        Should.Throw<DomainException>(() => order.Hold("admin-1", " ", _later));
        Should.Throw<DomainException>(() => order.Block("admin-1", null, _later));
        Should.Throw<DomainException>(() => order.Fail("admin-1", "", _later));
        Should.Throw<DomainException>(() => OrderBuilder.InStatus(OrderStatus.Confirmed).Cancel("admin-1", " ", _later));
        order.Status.ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Transition_BlankActor_Throws()
    {
        var order = OrderBuilder.Pending();

        Should.Throw<DomainException>(() => order.Confirm(" ", _later));
        order.Status.ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Transition_Sequence_KeepsTheFullHistoryInOrder()
    {
        var order = OrderBuilder.Pending();

        order.Hold("system:worker", "BRL above 5,000", _later);
        order.Confirm("admin-1", _later.AddMinutes(1), "reviewed");
        order.Cancel("admin-1", "customer asked", _later.AddMinutes(2));

        order.StatusHistory.Select(change => change.To).ShouldBe(
            [OrderStatus.Pending, OrderStatus.OnHold, OrderStatus.Confirmed, OrderStatus.Cancelled]);
        order.RowVersion.ShouldBe(3);
    }

    private static void Apply(Order order, OrderStatus target, string actor, string reason)
    {
        switch (target)
        {
            case OrderStatus.Confirmed:
                order.Confirm(actor, _later, reason);
                break;
            case OrderStatus.OnHold:
                order.Hold(actor, reason, _later);
                break;
            case OrderStatus.Blocked:
                order.Block(actor, reason, _later);
                break;
            case OrderStatus.Failed:
                order.Fail(actor, reason, _later);
                break;
            case OrderStatus.Cancelled:
                order.Cancel(actor, reason, _later);
                break;
            default:
                throw new InvalidOperationException("Pending is never a target.");
        }
    }

    private static TheoryData<OrderStatus, OrderStatus> ToTheoryData(IEnumerable<(OrderStatus From, OrderStatus To)> pairs)
    {
        var data = new TheoryData<OrderStatus, OrderStatus>();

        foreach (var (from, to) in pairs)
        {
            data.Add(from, to);
        }

        return data;
    }
}
