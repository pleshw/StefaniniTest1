using System.Diagnostics;
using Catalog.Domain;

namespace Catalog.Application.Products;

/// <summary>Pieces shared by the handlers that change an existing product.</summary>
internal static class ProductChanges
{
    public static IReadOnlyCollection<PriceHistoryEntry> HistoryFor(Product product, AuditStamp audit) => product.DomainEvents
        .OfType<PriceChanged>()
        .Select(change => new PriceHistoryEntry(
            change.Sku,
            change.NewPrice.Currency,
            change.OldPrice?.Amount,
            change.NewPrice.Amount,
            change.Version,
            audit.UpdatedBy,
            audit.UpdatedAt,
            Activity.Current?.TraceId.ToString()))
        .ToList();
}
