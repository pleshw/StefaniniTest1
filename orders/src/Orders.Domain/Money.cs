using FluentValidation;
using Orders.Domain.Validation;

namespace Orders.Domain;

/// <summary>A non-negative amount in one currency, rounded to two decimals (away from zero) whenever it is created.</summary>
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

    public static Money Zero(Currency currency) => new(0m, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return Create(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return Create(Amount - other.Amount, Currency);
    }

    public Money Multiply(int quantity) => Create(Amount * quantity, Currency);

    /// <summary><paramref name="percent"/> percent of this amount, rounded like every amount.</summary>
    public Money Percent(decimal percent) => Create(Amount * percent / 100m, Currency);

    public Money Min(Money other)
    {
        EnsureSameCurrency(other);
        return other.Amount < Amount ? other : this;
    }

    public bool IsGreaterThan(Money other)
    {
        EnsureSameCurrency(other);
        return Amount > other.Amount;
    }

    private void EnsureSameCurrency(Money other)
    {
        if (other.Currency != Currency)
        {
            throw new DomainException($"Cannot combine {Currency} with {other.Currency}.");
        }
    }

    private sealed class AmountValidator : AbstractValidator<decimal>
    {
        public AmountValidator()
        {
            RuleFor(amount => amount)
                .GreaterThanOrEqualTo(0).WithMessage("Money must not be negative.")
                .OverridePropertyName(nameof(Money));
        }
    }
}
