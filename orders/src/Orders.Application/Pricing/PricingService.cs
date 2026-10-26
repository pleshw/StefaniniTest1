using FluentValidation;
using Orders.Application.Catalog;
using Orders.Domain;

namespace Orders.Application.Pricing;

public sealed record CartLine(string Sku, int Quantity);

/// <summary>A cart line priced by Catalog. The category stays for discount conditions that arrive later.</summary>
public sealed record PricedLine(string Sku, string ProductName, string Category, Money UnitPrice, int CatalogVersion, int Quantity)
{
    public OrderItem ToOrderItem(Money? lineDiscount = null) =>
        OrderItem.Create(Sku, ProductName, UnitPrice, CatalogVersion, Quantity, lineDiscount);
}

public sealed record PricedCart(Currency Currency, IReadOnlyList<PricedLine> Lines);

/// <summary>A cart as the shopper sent it, before it is validated and priced.</summary>
public sealed record Cart(IReadOnlyList<CartLine> Lines);

public sealed class CartLineValidator : AbstractValidator<CartLine>
{
    public CartLineValidator()
    {
        RuleFor(line => line.Sku).NotEmpty().WithMessage("Every line needs a SKU.");
        RuleFor(line => line.Quantity)
            .GreaterThan(0)
            .WithMessage(line => $"The quantity of {PricingService.NormalizeSku(line.Sku)} must be greater than zero.");
    }
}

public sealed class CartValidator : AbstractValidator<Cart>
{
    public CartValidator(IValidator<CartLine> lineValidator)
    {
        RuleFor(cart => cart.Lines)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("The cart is empty.");
        RuleForEach(cart => cart.Lines).SetValidator(lineValidator);
        RuleFor(cart => cart.Lines)
            .Must(lines => lines.GroupBy(line => PricingService.NormalizeSku(line.Sku)).All(group => group.Sum(line => (long)line.Quantity) <= int.MaxValue))
            .WithMessage("A SKU has too large a quantity.")
            .Must(lines => lines.Select(line => PricingService.NormalizeSku(line.Sku)).Distinct().Count() <= PricingService.MaxDistinctSkus)
            .WithMessage($"A cart can have at most {PricingService.MaxDistinctSkus} distinct SKUs.")
            .When(cart => cart.Lines.Count > 0);
    }
}

/// <summary>Prices a cart with one Catalog call. Catalog is the price authority: a price never comes from the client.</summary>
public sealed class PricingService(ICatalogPriceProvider catalog, IValidator<Cart> validator)
{
    /// <summary>The batch limit of Catalog's price query.</summary>
    public const int MaxDistinctSkus = 100;

    public async Task<Result<PricedCart>> PriceAsync(Currency currency, IReadOnlyList<CartLine> lines, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(new Cart(lines), cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<PricedCart>();
        }

        var merged = Merge(lines);
        var response = await catalog.GetPricesAsync(currency, merged.Keys.ToList(), cancellationToken);

        if (!response.IsSuccess)
        {
            return Result.Fail<PricedCart>(response.Error!.Kind, response.Error.Message);
        }

        var prices = response.Value.Found.ToDictionary(price => price.Sku, StringComparer.Ordinal);
        var problems = FindProblems(currency, merged.Keys, prices);

        return problems is null
            ? Result.Ok(new PricedCart(currency, merged.Select(line => ToPricedLine(prices[line.Key], line.Value)).ToList()))
            : Result.Fail<PricedCart>(ErrorKind.Unprocessable, problems);
    }

    internal static string NormalizeSku(string? sku) => sku?.Trim().ToUpperInvariant() ?? string.Empty;

    /// <summary>Sums the quantities of lines with the same SKU, keeping the order in which SKUs first appear.</summary>
    private static Dictionary<string, int> Merge(IReadOnlyList<CartLine> lines)
    {
        var merged = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var sku = NormalizeSku(line.Sku);
            merged[sku] = merged.GetValueOrDefault(sku) + line.Quantity;
        }

        return merged;
    }

    private static string? FindProblems(Currency currency, IEnumerable<string> requested, Dictionary<string, CatalogPrice> prices)
    {
        var skus = requested.ToList();
        var missing = skus.Where(sku => !prices.ContainsKey(sku)).ToList();
        var inactive = skus.Where(sku => prices.TryGetValue(sku, out var price) && !price.IsActive).ToList();
        var wrongCurrency = skus.Where(sku => prices.TryGetValue(sku, out var price) && price.UnitPrice.Currency != currency).ToList();

        var problems = new List<string>();
        AddProblem(problems, "Unknown SKUs or SKUs without a price in " + currency, missing);
        AddProblem(problems, "Inactive SKUs", inactive);
        AddProblem(problems, "SKUs priced in another currency than " + currency, wrongCurrency);

        return problems.Count == 0 ? null : string.Join(". ", problems) + ".";
    }

    private static void AddProblem(List<string> problems, string label, List<string> skus)
    {
        if (skus.Count > 0)
        {
            problems.Add($"{label}: {string.Join(", ", skus)}");
        }
    }

    private static PricedLine ToPricedLine(CatalogPrice price, int quantity) =>
        new(price.Sku, price.Name, price.Category, price.UnitPrice, price.Version, quantity);
}
