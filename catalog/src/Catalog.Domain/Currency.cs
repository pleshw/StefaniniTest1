namespace Catalog.Domain;

/// <summary>An ISO 4217 alphabetic currency code. Which codes the catalog accepts is decided by <see cref="CurrencyPolicy"/>.</summary>
public sealed record Currency
{
    private const int _codeLength = 3;

    private Currency(string code)
    {
        Code = code;
    }

    public string Code { get; }

    public static Currency Create(string? code)
    {
        var normalized = code?.Trim().ToUpperInvariant();

        if (normalized is not { Length: _codeLength } || !normalized.All(char.IsAsciiLetterUpper))
        {
            throw new DomainException("Currency must be a three-letter ISO 4217 code.");
        }

        return new Currency(normalized);
    }

    public override string ToString() => Code;
}
