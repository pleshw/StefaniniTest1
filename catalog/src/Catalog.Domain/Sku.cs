namespace Catalog.Domain;

public sealed record Sku
{
    public const int MaxLength = 32;

    private Sku(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static Sku Create(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(normalized))
        {
            throw new DomainException("SKU must not be empty.");
        }

        if (normalized.Length > MaxLength)
        {
            throw new DomainException($"SKU must have at most {MaxLength} characters.");
        }

        return new Sku(normalized);
    }

    public override string ToString() => Value;
}
