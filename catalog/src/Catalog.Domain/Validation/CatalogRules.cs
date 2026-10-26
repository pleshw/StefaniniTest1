using FluentValidation;

namespace Catalog.Domain.Validation;

/// <summary>Rules shared by every validator that reads catalog identifiers from raw input.</summary>
public static class CatalogRules
{
    public static IRuleBuilderOptions<T, string?> ValidSku<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(Sku.IsValid).WithMessage("'{PropertyValue}' is not a valid SKU.");

    public static IRuleBuilderOptions<T, string?> SupportedCurrency<T>(this IRuleBuilder<T, string?> rule, CurrencyPolicy currencies) =>
        rule.Must(code => currencies.TryParse(code, out _)).WithMessage("Currency '{PropertyValue}' is not supported.");
}
