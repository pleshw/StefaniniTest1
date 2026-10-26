using MongoDB.Bson;

namespace Catalog.Infrastructure.Persistence;

internal static class CatalogCollections
{
    public const string Products = "products";
    public const string PriceHistory = "price_history";
    public const string IdempotencyRequests = "idempotency_requests";
}

internal sealed class ProductDocument
{
    public ObjectId Id { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public Dictionary<string, string> Attributes { get; set; } = [];

    public List<PriceDocument> Prices { get; set; } = [];

    public bool IsActive { get; set; }

    public int Version { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

internal sealed class PriceDocument
{
    public string Currency { get; set; } = string.Empty;

    public decimal Amount { get; set; }
}

internal sealed class PriceHistoryDocument
{
    public ObjectId Id { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public decimal? OldPrice { get; set; }

    public decimal NewPrice { get; set; }

    public int Version { get; set; }

    public string ChangedBy { get; set; } = string.Empty;

    public DateTime ChangedAt { get; set; }

    public string? TraceId { get; set; }
}

/// <summary>Shape of the idempotency store (plan: Catalog data model); it is only indexed in this step.</summary>
internal sealed class IdempotencyDocument
{
    public ObjectId Id { get; set; }

    public string Scope { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string RequestHash { get; set; } = string.Empty;

    public int ResponseStatus { get; set; }

    public string ResponseBody { get; set; } = string.Empty;

    public string ResourceId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }
}
