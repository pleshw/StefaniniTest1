namespace Catalog.Domain;

/// <summary>The currencies the catalog keeps prices in; the list comes from configuration.</summary>
public sealed class CurrencyPolicy
{
    private readonly HashSet<Currency> _allowed;

    public CurrencyPolicy(IEnumerable<string> allowedCodes)
    {
        var currencies = allowedCodes.Select(Currency.Create).ToList();

        if (currencies.Count == 0)
        {
            throw new DomainException("At least one currency must be allowed.");
        }

        _allowed = currencies.ToHashSet();
        Default = currencies[0];
    }

    /// <summary>The first configured currency, used when a request names none.</summary>
    public Currency Default { get; }

    public Currency Parse(string? code) =>
        TryParse(code, out var currency)
            ? currency!
            : throw new DomainException($"Currency {code} is not supported.");

    public bool TryParse(string? code, out Currency? currency)
    {
        currency = Currency.TryCreate(code, out var parsed) && _allowed.Contains(parsed!) ? parsed : null;

        return currency is not null;
    }
}
