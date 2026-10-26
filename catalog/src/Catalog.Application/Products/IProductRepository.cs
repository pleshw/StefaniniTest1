using Catalog.Domain;

namespace Catalog.Application.Products;

public interface IProductRepository
{
    Task<Product?> GetBySkuAsync(Sku sku, CancellationToken cancellationToken);

    /// <summary>Reads the price-relevant fields of many products with a single query.</summary>
    Task<IReadOnlyList<ProductSummary>> GetManyAsync(IReadOnlyCollection<Sku> skus, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts the product when <paramref name="expectedVersion"/> is null, otherwise updates it only if the stored
    /// version still equals <paramref name="expectedVersion"/>. The price history rows are written in the same
    /// transaction. Throws <see cref="ConcurrencyException"/> when the product exists already or the version moved.
    /// </summary>
    Task SaveAsync(
        Product product,
        int? expectedVersion,
        AuditStamp audit,
        IReadOnlyCollection<PriceHistoryEntry> priceHistory,
        CancellationToken cancellationToken);
}
