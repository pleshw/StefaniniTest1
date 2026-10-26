using FluentValidation;
using Orders.Domain.Validation;

namespace Orders.Domain;

public sealed class Order
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> _allowedTransitions = new()
    {
        [OrderStatus.Pending] = [OrderStatus.Confirmed, OrderStatus.OnHold, OrderStatus.Blocked, OrderStatus.Failed],
        [OrderStatus.OnHold] = [OrderStatus.Confirmed, OrderStatus.Blocked],
        [OrderStatus.Confirmed] = [OrderStatus.Cancelled],
    };

    private static readonly BuildValidator _buildValidator = new();
    private static readonly TransitionValidator _transitionValidator = new();

    private readonly List<OrderItem> _items;
    private readonly List<OrderStatusChange> _history = [];
    private readonly List<IDomainEvent> _domainEvents = [];

    private Order(
        Guid id,
        string number,
        ClientRef client,
        string createdBy,
        OrderChannel channel,
        List<OrderItem> items,
        Currency currency,
        DateTimeOffset createdAt)
    {
        Id = id;
        Number = number;
        Client = client;
        CreatedBy = createdBy;
        Channel = channel;
        _items = items;
        Currency = currency;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Status = OrderStatus.Pending;

        ItemCount = items.Sum(item => item.Quantity);
        Subtotal = items.Aggregate(Money.Zero(currency), (sum, item) => sum.Add(item.LineSubtotal));
        DiscountTotal = items.Aggregate(Money.Zero(currency), (sum, item) => sum.Add(item.LineDiscount));
        Total = Subtotal.Subtract(DiscountTotal);
    }

    public Guid Id { get; }

    public string Number { get; }

    public ClientRef Client { get; }

    public string CreatedBy { get; }

    public OrderChannel Channel { get; }

    public OrderStatus Status { get; private set; }

    public Currency Currency { get; }

    /// <summary>The sum of the item quantities.</summary>
    public int ItemCount { get; }

    public Money Subtotal { get; }

    public Money DiscountTotal { get; }

    public Money Total { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Starts at 0 and grows with every status change; the optimistic concurrency token.</summary>
    public int RowVersion { get; private set; }

    public IReadOnlyList<OrderItem> Items => _items;

    public IReadOnlyList<OrderStatusChange> StatusHistory => _history;

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public static Order Place(
        Guid id,
        string number,
        ClientRef client,
        string createdBy,
        OrderChannel channel,
        IEnumerable<OrderItem> items,
        DateTimeOffset now)
    {
        var order = Build(id, number, client, createdBy, channel, items, now);
        order._history.Add(new OrderStatusChange(null, OrderStatus.Pending, null, order.CreatedBy, now));
        order._domainEvents.Add(new OrderPlaced(id, order.Number, client.Id, channel, order.Total, order.ItemCount, now));

        return order;
    }

    /// <summary>Rebuilds a stored order. The totals are recomputed from the items, and no event is raised.</summary>
    public static Order Restore(
        Guid id,
        string number,
        ClientRef client,
        string createdBy,
        OrderChannel channel,
        OrderStatus status,
        IEnumerable<OrderItem> items,
        IEnumerable<OrderStatusChange> history,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        int rowVersion)
    {
        var order = Build(id, number, client, createdBy, channel, items, createdAt);
        order.Status = status;
        order.UpdatedAt = updatedAt;
        order.RowVersion = rowVersion;
        order._history.AddRange(history);

        return order;
    }

    public void Confirm(string actor, DateTimeOffset now, string? reason = null) =>
        TransitionTo(OrderStatus.Confirmed, actor, reason, now, reasonRequired: false);

    public void Hold(string actor, string? reason, DateTimeOffset now) =>
        TransitionTo(OrderStatus.OnHold, actor, reason, now, reasonRequired: true);

    public void Block(string actor, string? reason, DateTimeOffset now) =>
        TransitionTo(OrderStatus.Blocked, actor, reason, now, reasonRequired: true);

    public void Fail(string actor, string? reason, DateTimeOffset now) =>
        TransitionTo(OrderStatus.Failed, actor, reason, now, reasonRequired: true);

    public void Cancel(string actor, string? reason, DateTimeOffset now) =>
        TransitionTo(OrderStatus.Cancelled, actor, reason, now, reasonRequired: true);

    public void ClearDomainEvents() => _domainEvents.Clear();

    private static Order Build(
        Guid id,
        string number,
        ClientRef client,
        string createdBy,
        OrderChannel channel,
        IEnumerable<OrderItem> items,
        DateTimeOffset createdAt)
    {
        var orderNumber = number?.Trim();
        var creator = createdBy?.Trim();
        var lines = items.ToList();
        _buildValidator.EnsureValid(new BuildInput(id, orderNumber, creator, lines));

        var currency = lines[0].UnitPrice.Currency;

        return new Order(id, orderNumber!, client, creator!, channel, lines, currency, createdAt);
    }

    private void TransitionTo(OrderStatus target, string actor, string? reason, DateTimeOffset now, bool reasonRequired)
    {
        var changedBy = actor?.Trim();
        _transitionValidator.EnsureValid(new TransitionInput(changedBy, reason, reasonRequired));

        if (!_allowedTransitions.TryGetValue(Status, out var allowed) || !allowed.Contains(target))
        {
            throw new InvalidStatusTransitionException(Status, target);
        }

        var previous = Status;
        Status = target;
        UpdatedAt = now;
        RowVersion++;
        _history.Add(new OrderStatusChange(previous, target, reason, changedBy!, now));
        _domainEvents.Add(new OrderStatusChanged(Id, previous, target, reason, changedBy!, now));
    }

    private sealed record BuildInput(Guid Id, string? Number, string? CreatedBy, List<OrderItem> Lines);

    private sealed class BuildValidator : AbstractValidator<BuildInput>
    {
        public BuildValidator()
        {
            RuleFor(input => input.Id).NotEqual(Guid.Empty).WithMessage("Order id must not be empty.");
            RuleFor(input => input.Number).NotEmpty().WithMessage("Order number must not be empty.");
            RuleFor(input => input.CreatedBy).NotEmpty().WithMessage("Creator must not be empty.");
            RuleFor(input => input.Lines)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("An order needs at least one item.")
                .Must(lines => lines.TrueForAll(line => line.UnitPrice.Currency == lines[0].UnitPrice.Currency))
                .WithMessage("All items of an order must use the same currency.");
        }
    }

    private sealed record TransitionInput(string? Actor, string? Reason, bool ReasonRequired);

    private sealed class TransitionValidator : AbstractValidator<TransitionInput>
    {
        public TransitionValidator()
        {
            RuleFor(input => input.Actor).NotEmpty().WithMessage("Actor must not be empty.");
            RuleFor(input => input.Reason).NotEmpty().When(input => input.ReasonRequired).WithMessage("A reason must not be empty.");
        }
    }
}
