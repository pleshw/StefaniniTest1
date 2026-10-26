namespace Orders.Domain;

/// <param name="From">Null for the first entry, written when the order is placed.</param>
public sealed record OrderStatusChange(OrderStatus? From, OrderStatus To, string? Reason, string Actor, DateTimeOffset ChangedAt);
