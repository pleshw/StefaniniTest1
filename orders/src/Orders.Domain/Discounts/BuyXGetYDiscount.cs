using FluentValidation;

namespace Orders.Domain.Discounts;

/// <param name="Skus">The SKUs whose units count and can be free; null means every SKU.</param>
public sealed record BuyXGetYParameters(int Buy, int Get, IReadOnlyList<string>? Skus = null);

internal sealed class BuyXGetYParametersValidator : AbstractValidator<BuyXGetYParameters>
{
    public BuyXGetYParametersValidator()
    {
        RuleFor(parameters => parameters.Buy).GreaterThanOrEqualTo(1).WithMessage("buy must be at least 1.");
        RuleFor(parameters => parameters.Get).GreaterThanOrEqualTo(1).WithMessage("get must be at least 1.");
        RuleFor(parameters => parameters.Skus).ValidSkuList();
    }
}

/// <summary>
/// For every <c>buy + get</c> qualifying units, <c>get</c> units are free, always the cheapest ones (ties by SKU), so
/// the discount lands on the lines that hold them.
/// </summary>
public sealed class BuyXGetYDiscount(BuyXGetYParameters parameters) : IDiscountStrategy
{
    private readonly HashSet<string>? _skus = parameters.Skus?.Select(DiscountRules.NormalizeSku).ToHashSet(StringComparer.Ordinal);

    public DiscountType Type => DiscountType.BuyXGetY;

    public BuyXGetYParameters Parameters { get; } = parameters;

    public DiscountResult Calculate(DiscountCart cart)
    {
        var qualifying = cart.Lines
            .Where(line => _skus is null || _skus.Contains(line.Sku))
            .OrderBy(line => line.UnitPrice.Amount)
            .ThenBy(line => line.Sku, StringComparer.Ordinal)
            .ToList();
        var units = qualifying.Sum(line => (long)line.Quantity);
        var free = units / (Parameters.Buy + (long)Parameters.Get) * Parameters.Get;
        var amounts = new List<DiscountLineAmount>();

        foreach (var line in qualifying)
        {
            if (free == 0)
            {
                break;
            }

            var freeHere = (int)Math.Min(line.Quantity, free);
            amounts.Add(new DiscountLineAmount(line.Sku, line.UnitPrice.Multiply(freeHere)));
            free -= freeHere;
        }

        return DiscountResult.ForLines(cart.Currency, amounts);
    }
}
