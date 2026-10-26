using Catalog.Domain;

namespace Catalog.Application.Products;

public sealed record ProductSummary(
    Sku Sku,
    string Name,
    string Category,
    bool IsActive,
    int Version,
    IReadOnlyList<Money> Prices);

public sealed record AuditStamp(string UpdatedBy, DateTimeOffset UpdatedAt);

public sealed record PriceHistoryEntry(
    Sku Sku,
    Currency Currency,
    decimal? OldPrice,
    decimal NewPrice,
    int Version,
    string ChangedBy,
    DateTimeOffset ChangedAt,
    string? TraceId);

public sealed class ConcurrencyException(string message) : Exception(message);
