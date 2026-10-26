using Catalog.Domain;
using Catalog.Domain.Validation;
using FluentValidation;

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

public sealed class PriceQueryValidator : AbstractValidator<PriceQuery>
{
    public PriceQueryValidator(CurrencyPolicy currencies)
    {
        RuleFor(query => query.Currency)
            .SupportedCurrency(currencies)
            .WithMessage("currency must be one of the supported currencies, got '{PropertyValue}'.");
        RuleFor(query => query.Skus)
            .Must(skus => skus is { Count: >= 1 and <= QueryPricesHandler.MaxSkus })
            .WithMessage($"skus must contain between 1 and {QueryPricesHandler.MaxSkus} items.");

        When(query => query.Skus is { Count: >= 1 and <= QueryPricesHandler.MaxSkus }, () =>
        {
            RuleForEach(query => query.Skus).ValidSku();
            RuleFor(query => query.Skus!)
                .Must(BeDistinct)
                .WithMessage("skus must be distinct.");
        });
    }

    private static bool BeDistinct(IReadOnlyList<string> values)
    {
        var skus = values.Where(Sku.IsValid).Select(Sku.Create).ToList();

        return skus.Distinct().Count() == skus.Count;
    }
}

public sealed class QueryPricesHandler(IProductRepository repository, CurrencyPolicy currencies, IValidator<PriceQuery> validator)
{
    public const int MaxSkus = 100;

    public async Task<Result<PriceQueryResult>> HandleAsync(PriceQuery query, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<PriceQueryResult>();
        }

        var currency = currencies.Parse(query.Currency);
        var skus = query.Skus!.Select(Sku.Create).ToList();
        var found = (await repository.GetManyAsync(skus, cancellationToken)).ToDictionary(product => product.Sku);
        var items = new List<PriceQueryItem>();
        var missing = new List<string>();

        foreach (var sku in skus)
        {
            var product = found.GetValueOrDefault(sku);
            var price = product?.Prices.FirstOrDefault(candidate => candidate.Currency == currency);

            if (price is null)
            {
                missing.Add(sku.Value);
                continue;
            }

            items.Add(product!.ToPriceQueryItem(price.Amount, price.Currency.Code));
        }

        return Result.Ok(new PriceQueryResult(items, missing));
    }
}
