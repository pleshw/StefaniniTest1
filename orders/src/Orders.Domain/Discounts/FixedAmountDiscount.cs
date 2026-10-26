using FluentValidation;

namespace Orders.Domain.Discounts;

public sealed record CurrencyAmount(string Currency, decimal Amount);

/// <param name="Amounts">One amount per currency; a cart in a currency not listed gets nothing.</param>
public sealed record FixedAmountParameters(IReadOnlyList<CurrencyAmount> Amounts);

internal sealed class FixedAmountParametersValidator : AbstractValidator<FixedAmountParameters>
{
    public FixedAmountParametersValidator()
    {
        RuleFor(parameters => parameters.Amounts).NotEmpty().WithMessage("amounts needs at least one currency.");
        RuleForEach(parameters => parameters.Amounts).ChildRules(amount =>
        {
            amount.RuleFor(item => item.Currency).ValidCurrencyCode();
            amount.RuleFor(item => item.Amount).GreaterThan(0m).WithMessage("Every amount must be greater than 0.");
        });
        RuleFor(parameters => parameters.Amounts)
            .Must(amounts => amounts.Select(item => item.Currency.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count() == amounts.Count)
            .WithMessage("amounts has one entry per currency.");
    }
}

/// <summary>A fixed amount off the order in the cart currency, never more than the subtotal.</summary>
public sealed class FixedAmountDiscount(FixedAmountParameters parameters) : IDiscountStrategy
{
    public DiscountType Type => DiscountType.FixedAmount;

    public FixedAmountParameters Parameters { get; } = parameters;

    public DiscountResult Calculate(DiscountCart cart)
    {
        var amount = Parameters.Amounts.FirstOrDefault(item => Currency.Create(item.Currency) == cart.Currency);

        return amount is null
            ? DiscountResult.None(cart.Currency)
            : DiscountResult.ForOrder(Money.Create(amount.Amount, cart.Currency).Min(cart.Subtotal));
    }
}
