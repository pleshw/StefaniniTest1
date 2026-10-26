namespace Orders.Contracts;

// Events published to the topic exchange orders.events. The version is part of the type name: a breaking change is a
// new type (OrderPlacedV2) published beside the old one until every consumer has moved. Only records live here, with
// primitive members, so consumers need no reference to the Orders Domain.

/// <summary>An order was placed. Routing key <c>order.placed</c>.</summary>
/// <param name="Channel">"shop" or "admin".</param>
/// <param name="Currency">ISO 4217 code of <paramref name="Total"/>.</param>
public sealed record OrderPlacedV1(
    Guid OrderId,
    string Number,
    Guid ClientId,
    string Channel,
    string Currency,
    decimal Total,
    int ItemCount,
    DateTimeOffset OccurredAt);

/// <summary>An order moved to another status. Routing key <c>order.status-changed</c>.</summary>
/// <param name="From">The previous status name, such as "Pending".</param>
/// <param name="To">The new status name, such as "OnHold".</param>
/// <param name="Actor">A user id, or "system:worker" for the automatic rules.</param>
public sealed record OrderStatusChangedV1(
    Guid OrderId,
    string From,
    string To,
    string? Reason,
    string Actor,
    DateTimeOffset OccurredAt);
