namespace Orders.Domain;

public sealed record OrderPlaced(
    Guid OrderId,
    string Number,
    Guid ClientId,
    OrderChannel Channel,
    Money Total,
    int ItemCount,
    DateTimeOffset OccurredAt) : IDomainEvent;
