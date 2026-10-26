using FluentValidation;
using FluentValidation.Results;
using Orders.Domain.Validation;

namespace Orders.Domain.Discounts;

/// <summary>A rule type bound to its validated parameters: computes what the rule takes off a cart.</summary>
public interface IDiscountStrategy
{
    DiscountType Type { get; }

    DiscountResult Calculate(DiscountCart cart);
}

/// <summary>
/// Reads a rule's <c>parameters</c> JSON into the typed parameters of its type. The JSON is strict: unknown or missing
/// fields, wrong value types and unknown enum names fail; then the type's validator checks the values.
/// </summary>
public static class DiscountStrategies
{
    public static ValidationResult Validate(DiscountType type, string? parameters) => Parse(type, parameters).Validation;

    /// <summary>The strategy of <paramref name="type"/> with these parameters; throws <see cref="DomainException"/> when they are invalid.</summary>
    public static IDiscountStrategy Create(DiscountType type, string? parameters)
    {
        var (strategy, validation) = Parse(type, parameters);

        return validation.IsValid ? strategy! : throw new DomainException(validation.ToString(" "));
    }

    private static (IDiscountStrategy? Strategy, ValidationResult Validation) Parse(DiscountType type, string? parameters) => type switch
    {
        DiscountType.Percentage => StrictJson.Parse(parameters, new PercentageParametersValidator(), IDiscountStrategy (p) => new PercentageDiscount(p)),
        DiscountType.FixedAmount => StrictJson.Parse(parameters, new FixedAmountParametersValidator(), IDiscountStrategy (p) => new FixedAmountDiscount(p)),
        DiscountType.BuyXGetY => StrictJson.Parse(parameters, new BuyXGetYParametersValidator(), IDiscountStrategy (p) => new BuyXGetYDiscount(p)),
        DiscountType.Tiered => StrictJson.Parse(parameters, new TieredParametersValidator(), IDiscountStrategy (p) => new TieredDiscount(p)),
        _ => (null, StrictJson.Failure("type", $"Unknown discount type {type}.")),
    };
}

internal static class DiscountRules
{
    public const decimal MaxPercent = 100m;

    public static IRuleBuilderOptions<T, decimal> ValidPercent<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.GreaterThan(0m).LessThanOrEqualTo(MaxPercent).WithMessage($"percent must be greater than 0 and at most {MaxPercent}.");

    public static IRuleBuilderOptions<T, string> ValidCurrencyCode<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(code => Currency.TryCreate(code, out _)).WithMessage("Currency '{PropertyValue}' must be a three-letter ISO 4217 code.");

    public static IRuleBuilderOptions<T, IReadOnlyList<string>?> ValidSkuList<T>(this IRuleBuilder<T, IReadOnlyList<string>?> rule) =>
        rule.Must(skus => skus is null || (skus.Count > 0 && skus.All(sku => !string.IsNullOrWhiteSpace(sku))))
            .WithMessage("skus lists at least one non-empty SKU.");

    public static string NormalizeSku(string sku) => sku.Trim().ToUpperInvariant();
}
