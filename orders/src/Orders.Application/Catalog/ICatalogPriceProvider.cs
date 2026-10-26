using Orders.Domain;

namespace Orders.Application.Catalog;

/// <summary>The current price of one product, as Catalog reports it. Catalog is the price authority.</summary>
public sealed record CatalogPrice(string Sku, string Name, string Category, Money UnitPrice, int Version, bool IsActive);

/// <param name="Missing">Requested SKUs Catalog does not know, or that have no price in the currency.</param>
public sealed record CatalogPrices(IReadOnlyList<CatalogPrice> Found, IReadOnlyList<string> Missing);

public interface ICatalogPriceProvider
{
    /// <summary>One call for all SKUs. Fails with <see cref="ErrorKind.CatalogUnavailable"/> when Catalog cannot answer.</summary>
    Task<Result<CatalogPrices>> GetPricesAsync(Currency currency, IReadOnlyCollection<string> skus, CancellationToken cancellationToken);
}
