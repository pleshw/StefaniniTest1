using System.Collections.Concurrent;
using Catalog.Application.Products;
using Catalog.Domain;

namespace Catalog.IntegrationTests;

/// <summary>Stands in for MongoDB in endpoint tests that exercise HTTP behaviour only.</summary>
internal sealed class InMemoryProductRepository : IProductRepository
{
    private readonly ConcurrentDictionary<string, Product> _products = new();

    public void Add(Product product) => _products[product.Sku.Value] = product;

    public Task<Product?> GetBySkuAsync(Sku sku, CancellationToken cancellationToken) =>
        Task.FromResult(_products.GetValueOrDefault(sku.Value));

    public Task<IReadOnlyList<ProductSummary>> GetManyAsync(IReadOnlyCollection<Sku> skus, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProductSummary> found = skus
            .Select(sku => _products.GetValueOrDefault(sku.Value))
            .OfType<Product>()
            .Select(product => new ProductSummary(
                product.Sku, product.Name, product.Category, product.IsActive, product.Version, product.Prices))
            .ToList();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<Product>> SearchAsync(ProductSearch search, CancellationToken cancellationToken)
    {
        IReadOnlyList<Product> page = _products.Values
            .Where(product => product.IsActive)
            .Where(product => search.Category is null || product.Category == search.Category)
            .Where(product => search.Text is null
                || product.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)
                || product.Description.Contains(search.Text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(product => product.Name, StringComparer.Ordinal)
            .ThenBy(product => product.Sku.Value, StringComparer.Ordinal)
            .Where(product => search.After is null || IsAfter(product, search.After))
            .Take(search.Limit)
            .ToList();
        return Task.FromResult(page);
    }

    public Task SaveAsync(
        Product product,
        int? expectedVersion,
        AuditStamp audit,
        IReadOnlyCollection<PriceHistoryEntry> priceHistory,
        CancellationToken cancellationToken)
    {
        Add(product);
        return Task.CompletedTask;
    }

    private static bool IsAfter(Product product, ProductCursor cursor)
    {
        var byName = string.CompareOrdinal(product.Name, cursor.Name);
        return byName > 0 || (byName == 0 && string.CompareOrdinal(product.Sku.Value, cursor.Sku) > 0);
    }
}
