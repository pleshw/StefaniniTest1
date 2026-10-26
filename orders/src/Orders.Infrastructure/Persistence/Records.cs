using NpgsqlTypes;
using Orders.Domain;
using Orders.Domain.Discounts;

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

internal sealed class DiscountRuleRecord
{
    public Guid Id { get; set; }

    public int Version { get; set; }

    public string Name { get; set; } = string.Empty;

    public DiscountType Type { get; set; }

    public string Parameters { get; set; } = "{}";

    public string Conditions { get; set; } = "[]";

    public int Priority { get; set; }

    public bool Stackable { get; set; }

    public bool OncePerClient { get; set; }

    public DateTimeOffset ValidFrom { get; set; }

    public DateTimeOffset? ValidTo { get; set; }

    public bool IsActive { get; set; } = true;

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class OrderDiscountRecord
{
    public Guid OrderId { get; set; }

    public Guid DiscountRuleId { get; set; }

    public int RuleVersion { get; set; }

    public decimal Amount { get; set; }
}

internal sealed class DiscountRedemptionRecord
{
    public const string OncePerClientIndex = "ux_redemption_once_per_client";

    public Guid DiscountRuleId { get; set; }

    public Guid ClientId { get; set; }

    public Guid OrderId { get; set; }

    public bool OncePerClient { get; set; }

    public DateTimeOffset RedeemedAt { get; set; }
}
