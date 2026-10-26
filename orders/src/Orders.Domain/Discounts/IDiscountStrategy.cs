using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.Results;

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

/// <summary>Strict JSON reading shared by rule parameters and rule conditions.</summary>
internal static class StrictJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowOutOfOrderMetadataProperties = true,
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>Deserializes <paramref name="json"/>, validates it and builds the result; JSON errors become one failure.</summary>
    public static (TResult? Result, ValidationResult Validation) Parse<TValue, TResult>(
        string? json, IValidator<TValue> validator, Func<TValue, TResult> create, string propertyName = "parameters")
        where TValue : class
        where TResult : class
    {
        TValue? value;

        try
        {
            value = JsonSerializer.Deserialize<TValue>(json ?? string.Empty, Options);
        }
        catch (JsonException exception)
        {
            return (null, Failure(propertyName, $"The {propertyName} are not valid: {exception.Message}"));
        }

        if (value is null)
        {
            return (null, Failure(propertyName, $"The {propertyName} must not be null."));
        }

        var validation = validator.Validate(value);

        return (validation.IsValid ? create(value) : null, validation);
    }

    public static ValidationResult Failure(string propertyName, string message) => new([new ValidationFailure(propertyName, message)]);
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
