using Catalog.Domain;

namespace Catalog.Application.Products;

public sealed record PriceQuery(string? Currency, IReadOnlyList<string>? Skus);

public sealed record PriceQueryItem(
    string Sku,
    string Name,
    string Category,
    decimal UnitPrice,
    string Currency,
    int Version,
    bool IsActive);

/// <param name="Missing">SKUs that do not exist, or have no price in the requested currency.</param>
public sealed record PriceQueryResult(IReadOnlyList<PriceQueryItem> Items, IReadOnlyList<string> Missing);

public sealed class QueryPricesHandler(IProductRepository repository, CurrencyPolicy currencies)
{
    public const int MaxSkus = 100;

    public async Task<Result<PriceQueryResult>> HandleAsync(PriceQuery query, CancellationToken cancellationToken)
    {
        if (!currencies.TryParse(query.Currency, out var currency))
        {
            return Invalid($"currency must be one of the supported currencies, got '{query.Currency}'.");
        }

        if (query.Skus is not { Count: >= 1 and <= MaxSkus })
        {
            return Invalid($"skus must contain between 1 and {MaxSkus} items.");
        }

        var skus = new List<Sku>(query.Skus.Count);

        foreach (var value in query.Skus)
        {
            if (!Sku.TryCreate(value, out var sku))
            {
                return Invalid($"'{value}' is not a valid SKU.");
            }

            skus.Add(sku!);
        }

        if (skus.Distinct().Count() != skus.Count)
        {
            return Invalid("skus must be distinct.");
        }

        var found = (await repository.GetManyAsync(skus, cancellationToken)).ToDictionary(product => product.Sku);
        var items = new List<PriceQueryItem>();
        var missing = new List<string>();

        foreach (var sku in skus)
        {
            var price = found.GetValueOrDefault(sku)?.Prices.FirstOrDefault(candidate => candidate.Currency == currency);

            if (price is null)
            {
                missing.Add(sku.Value);
                continue;
            }

            var product = found[sku];
            items.Add(new PriceQueryItem(
                sku.Value, product.Name, product.Category, price.Amount, price.Currency.Code, product.Version, product.IsActive));
        }

        return Result.Ok(new PriceQueryResult(items, missing));
    }

    private static Result<PriceQueryResult> Invalid(string message) =>
        Result.Fail<PriceQueryResult>(ErrorKind.Validation, message);
}
