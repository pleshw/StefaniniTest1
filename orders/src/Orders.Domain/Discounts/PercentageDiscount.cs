using FluentValidation;

namespace Orders.Domain.Discounts;

public enum DiscountScope
{
    /// <summary>One amount on the subtotal, rounded once.</summary>
    Order,

    /// <summary>One amount per line, each rounded.</summary>
    Line,
}

/// <param name="Percent">Greater than 0, at most 100.</param>
public sealed record PercentageParameters(decimal Percent, DiscountScope AppliesTo);

internal sealed class PercentageParametersValidator : AbstractValidator<PercentageParameters>
{
    public PercentageParametersValidator()
    {
        RuleFor(parameters => parameters.Percent).ValidPercent();
        RuleFor(parameters => parameters.AppliesTo).IsInEnum();
    }
}

/// <summary>A percentage off the order subtotal or off every line; works in any currency.</summary>
public sealed class PercentageDiscount(PercentageParameters parameters) : IDiscountStrategy
{
    public DiscountType Type => DiscountType.Percentage;

    public PercentageParameters Parameters { get; } = parameters;

    public DiscountResult Calculate(DiscountCart cart) => Parameters.AppliesTo == DiscountScope.Order
        ? DiscountResult.ForOrder(cart.Subtotal.Percent(Parameters.Percent))
        : DiscountResult.ForLines(cart.Currency, cart.Lines.Select(line => new DiscountLineAmount(line.Sku, line.LineSubtotal.Percent(Parameters.Percent))));
}
