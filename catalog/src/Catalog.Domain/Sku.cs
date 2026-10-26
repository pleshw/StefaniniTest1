using Catalog.Domain.Validation;
using FluentValidation;

namespace Catalog.Domain;

public sealed record Sku
{
    public const int MaxLength = 32;

    private static readonly SkuValidator _validator = new();

    private Sku(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static Sku Create(string? value)
    {
        var normalized = Normalize(value);
        _validator.EnsureValid(normalized);

        return new Sku(normalized);
    }

    public static bool IsValid(string? value) => _validator.Validate(Normalize(value)).IsValid;

    public static bool TryCreate(string? value, out Sku? sku)
    {
        var normalized = Normalize(value);
        sku = _validator.Validate(normalized).IsValid ? new Sku(normalized) : null;

        return sku is not null;
    }

    public override string ToString() => Value;

    private static string Normalize(string? value) => value?.Trim().ToUpperInvariant() ?? string.Empty;

    private sealed class SkuValidator : AbstractValidator<string>
    {
        public SkuValidator()
        {
            RuleFor(value => value)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("SKU must not be empty.")
                .MaximumLength(MaxLength).WithMessage($"SKU must have at most {MaxLength} characters.")
                .OverridePropertyName(nameof(Sku));
        }
    }
}
