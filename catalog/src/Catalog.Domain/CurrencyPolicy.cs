namespace Catalog.Domain;

/// <summary>The currencies the catalog keeps prices in; the list comes from configuration.</summary>
public sealed class CurrencyPolicy
{
    private readonly HashSet<Currency> _allowed;

    public CurrencyPolicy(IEnumerable<string> allowedCodes)
    {
        _allowed = allowedCodes.Select(Currency.Create).ToHashSet();

        if (_allowed.Count == 0)
        {
            throw new DomainException("At least one currency must be allowed.");
        }
    }

    public Currency Parse(string? code)
    {
        var currency = Currency.Create(code);

        return _allowed.Contains(currency)
            ? currency
            : throw new DomainException($"Currency {currency.Code} is not supported.");
    }
}
