using Catalog.Domain.Validation;
using FluentValidation;

namespace Catalog.Domain;

public sealed record Money
{
    private const int _decimals = 2;

    private static readonly AmountValidator _validator = new();

    private Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    public static Money Create(decimal amount, Currency currency)
    {
        _validator.EnsureValid(amount);

        return new Money(Math.Round(amount, _decimals, MidpointRounding.AwayFromZero), currency);
    }

    private sealed class AmountValidator : AbstractValidator<decimal>
    {
        public AmountValidator()
        {
            RuleFor(amount => amount)
                .GreaterThanOrEqualTo(0).WithMessage("Price must not be negative.")
                .OverridePropertyName("Price");
        }
    }
}
