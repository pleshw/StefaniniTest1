using FluentValidation;

namespace Orders.Domain.Validation;

/// <summary>Rules shared by every validator that reads Orders identifiers from raw input.</summary>
public static class OrdersRules
{
    /// <summary>A null code passes (the caller has a default); anything else must be a three-letter ISO 4217 code.</summary>
    public static IRuleBuilderOptions<T, string?> ValidCurrencyOrDefault<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(code => code is null || Currency.TryCreate(code, out _)).WithMessage("Currency '{PropertyValue}' must be a three-letter ISO 4217 code.");
}
