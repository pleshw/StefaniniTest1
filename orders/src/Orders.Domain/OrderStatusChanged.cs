namespace Orders.Domain;

public sealed record OrderStatusChanged(
    Guid OrderId,
    OrderStatus From,
    OrderStatus To,
    string? Reason,
    string Actor,
    DateTimeOffset OccurredAt) : IDomainEvent;
