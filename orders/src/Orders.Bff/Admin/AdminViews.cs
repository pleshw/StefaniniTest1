using Orders.Bff.Clients.Orders;

namespace Orders.Bff.Admin;

/// <summary>
/// The order inspector: the order with its status history and its last events. A part that could not be read is null
/// and named in <see cref="Unavailable"/>, and <see cref="Partial"/> is true.
/// </summary>
internal sealed record AdminOrderView(
    OrderDto Order,
    IReadOnlyList<OrderStatusChangeDto>? History,
    IReadOnlyList<OrderEventDto>? Events,
    bool Partial,
    IReadOnlyList<string> Unavailable);

/// <summary>The OnHold queue (oldest first) next to the filtered order page; parts that failed are null and named.</summary>
internal sealed record AdminOrderList(
    IReadOnlyList<OrderSummaryDto>? OnHold,
    OrderPage? Orders,
    bool Partial,
    IReadOnlyList<string> Unavailable);
