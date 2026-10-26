using FluentValidation;
using Orders.Domain.Validation;

namespace Orders.Domain;

/// <summary>An ISO 4217 alphabetic currency code.</summary>
public sealed record Currency
{
    private const int _codeLength = 3;

    private static readonly CodeValidator _validator = new();

    private Currency(string code)
    {
        Code = code;
    }

    public string Code { get; }

    public static Currency Create(string? code)
    {
        var normalized = code?.Trim().ToUpperInvariant() ?? string.Empty;
        _validator.EnsureValid(normalized);

        return new Currency(normalized);
    }

    public override string ToString() => Code;

    private sealed class CodeValidator : AbstractValidator<string>
    {
        public CodeValidator()
        {
            RuleFor(code => code)
                .Must(code => code.Length == _codeLength && code.All(char.IsAsciiLetterUpper))
                .WithMessage("Currency must be a three-letter ISO 4217 code.")
                .OverridePropertyName(nameof(Currency));
        }
    }
}
