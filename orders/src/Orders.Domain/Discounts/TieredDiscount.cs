using FluentValidation;

namespace Orders.Domain.Discounts;

/// <param name="MinSubtotal">The band starts at this subtotal, inclusive.</param>
public sealed record DiscountTier(decimal MinSubtotal, decimal Percent);

/// <param name="Currency">The currency of the bands; a cart in another currency gets nothing.</param>
public sealed record TieredParameters(string Currency, IReadOnlyList<DiscountTier> Tiers);

internal sealed class TieredParametersValidator : AbstractValidator<TieredParameters>
{
    public TieredParametersValidator()
    {
        RuleFor(parameters => parameters.Currency).ValidCurrencyCode();
        RuleFor(parameters => parameters.Tiers).NotEmpty().WithMessage("tiers needs at least one band.");
        RuleForEach(parameters => parameters.Tiers).ChildRules(tier =>
        {
            tier.RuleFor(item => item.MinSubtotal).GreaterThanOrEqualTo(0m).WithMessage("minSubtotal must not be negative.");
            tier.RuleFor(item => item.Percent).ValidPercent();
        });
        RuleFor(parameters => parameters.Tiers)
            .Must(tiers => tiers.Select(tier => tier.MinSubtotal).Distinct().Count() == tiers.Count)
            .WithMessage("Every band needs its own minSubtotal.");
    }
}

/// <summary>A percentage off the order subtotal, taken from the highest band the subtotal reaches.</summary>
public sealed class TieredDiscount(TieredParameters parameters) : IDiscountStrategy
{
    private readonly Currency _currency = Currency.Create(parameters.Currency);

    public DiscountType Type => DiscountType.Tiered;

    public TieredParameters Parameters { get; } = parameters;

    public DiscountResult Calculate(DiscountCart cart)
    {
        var band = cart.Currency != _currency
            ? null
            : Parameters.Tiers.Where(tier => cart.Subtotal.Amount >= tier.MinSubtotal).MaxBy(tier => tier.MinSubtotal);

        return band is null ? DiscountResult.None(cart.Currency) : DiscountResult.ForOrder(cart.Subtotal.Percent(band.Percent));
    }
}
