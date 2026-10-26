namespace Catalog.Domain;

public sealed record Money
{
    private const int _decimals = 2;

    private Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    public static Money Create(decimal amount, Currency currency)
    {
        if (amount < 0)
        {
            throw new DomainException("Price must not be negative.");
        }

        return new Money(Math.Round(amount, _decimals, MidpointRounding.AwayFromZero), currency);
    }
}
