using NpgsqlTypes;
using Orders.Domain;

namespace Orders.Infrastructure.Persistence;

// Persistence records: plain mutable classes EF can materialize, kept apart so the Domain stays free of EF.

internal sealed class OrderRecord
{
    public Guid Id { get; set; }

    public string Number { get; set; } = string.Empty;

    public Guid ClientId { get; set; }

    public string ClientName { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;

    public OrderChannel Channel { get; set; }

    public OrderStatus Status { get; set; }

    public int ItemCount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal Subtotal { get; set; }

    public decimal DiscountTotal { get; set; }

    public decimal Total { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int RowVersion { get; set; }

    public NpgsqlTsVector? SearchVector { get; set; }

    public List<OrderItemRecord> Items { get; set; } = [];

    public List<OrderStatusHistoryRecord> History { get; set; } = [];
}

internal sealed class OrderItemRecord
{
    public Guid OrderId { get; set; }

    public int LineNo { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string ProductName { get; set; } = string.Empty;

    public decimal UnitPrice { get; set; }

    public int CatalogVersion { get; set; }

    public int Quantity { get; set; }

    public decimal LineDiscount { get; set; }

    public decimal LineTotal { get; set; }
}

internal sealed class OrderStatusHistoryRecord
{
    public Guid OrderId { get; set; }

    public int Seq { get; set; }

    public OrderStatus? FromStatus { get; set; }

    public OrderStatus ToStatus { get; set; }

    public string? Reason { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string? TraceId { get; set; }

    public DateTimeOffset ChangedAt { get; set; }
}

internal sealed class IdempotencyRequestRecord
{
    public Guid ClientId { get; set; }

    public Guid IdempotencyKey { get; set; }

    public byte[] RequestHash { get; set; } = [];

    public short ResponseStatus { get; set; }

    public string ResponseBody { get; set; } = "{}";

    public Guid OrderId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
