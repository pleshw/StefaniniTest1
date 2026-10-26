using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.Results;

namespace Orders.Domain.Validation;

/// <summary>
/// Strict JSON reading for rule data stored as JSON (discount parameters, rule conditions): unknown or missing fields,
/// wrong value types, numbers written as strings and integer enum values all fail.
/// </summary>
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
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // NotSupportedException: an abstract condition without its "kind" discriminator.
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
