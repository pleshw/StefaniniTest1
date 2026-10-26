namespace Orders.Application.Ordering;

public sealed record ClientDto(Guid Id, string Name);

public sealed record OrderItemDto(
    string Sku, string ProductName, decimal UnitPrice, int CatalogVersion, int Quantity, decimal LineDiscount, decimal LineTotal);

public sealed record OrderDto(
    Guid Id,
    string Number,
    ClientDto Client,
    string Status,
    string Channel,
    string Currency,
    int ItemCount,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal Total,
    IReadOnlyList<OrderItemDto> Items,
    DateTimeOffset CreatedAt);
