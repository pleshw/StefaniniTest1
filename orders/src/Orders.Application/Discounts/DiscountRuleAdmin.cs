using System.Text.Json;
using FluentValidation;
using Orders.Domain;
using Orders.Domain.Discounts;
using Orders.Domain.Rules;

namespace Orders.Application.Discounts;

/// <summary>One stored version of a discount rule as the admin API shows it. Parameters and conditions are the stored JSON.</summary>
/// <param name="Type">The <see cref="DiscountType"/> name.</param>
public sealed record DiscountRuleDto(
    Guid Id,
    int Version,
    string Name,
    string Type,
    JsonElement Parameters,
    JsonElement Conditions,
    int Priority,
    bool Stackable,
    bool OncePerClient,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    bool IsActive,
    string CreatedBy,
    DateTimeOffset CreatedAt);

/// <summary>
/// What an admin submits to create a rule or to replace it with a new version. <c>Parameters</c> and <c>Conditions</c>
/// are JSON text, checked by the strategy and the condition parsers of the Domain.
/// </summary>
/// <param name="Type">A <see cref="DiscountType"/> name.</param>
/// <param name="Conditions">A JSON array; null means no conditions.</param>
/// <param name="ValidFrom">The moment the rule starts; now when null.</param>
public sealed record RuleDefinition(
    string? Name,
    string? Type,
    string? Parameters,
    string? Conditions,
    int Priority,
    bool Stackable,
    bool OncePerClient,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    bool IsActive);

/// <summary>Checks a submitted rule: the name, the type, the parameters through the type's strategy and the conditions through <see cref="RuleConditions"/>.</summary>
public sealed class RuleDefinitionValidator : AbstractValidator<RuleDefinition>
{
    public const string NoConditions = "[]";

    public RuleDefinitionValidator()
    {
        RuleFor(rule => rule.Name).NotEmpty().WithMessage("Rule name must not be empty.");
        RuleFor(rule => rule.Type)
            .Must(type => RuleTypes.TryParse(type, out _))
            .WithMessage($"type must be one of {string.Join(", ", Enum.GetNames<DiscountType>())}.");
        RuleFor(rule => rule.Parameters)
            .Custom((parameters, context) =>
            {
                if (RuleTypes.TryParse(context.InstanceToValidate.Type, out var type))
                {
                    foreach (var error in DiscountStrategies.Validate(type, parameters).Errors)
                    {
                        context.AddFailure("parameters", $"parameters: {error.ErrorMessage}");
                    }
                }
            });
        RuleFor(rule => rule.Conditions)
            .Custom((conditions, context) =>
            {
                foreach (var error in RuleConditions.Validate(conditions ?? NoConditions).Errors)
                {
                    context.AddFailure("conditions", $"conditions: {error.ErrorMessage}");
                }
            });
        RuleFor(rule => rule.ValidTo)
            .GreaterThan(rule => rule.ValidFrom)
            .When(rule => rule.ValidFrom is not null && rule.ValidTo is not null)
            .WithMessage("validTo must be later than validFrom.");
    }
}

internal static class RuleTypes
{
    /// <summary>A type name as the API writes it; numbers are not accepted.</summary>
    public static bool TryParse(string? text, out DiscountType type)
    {
        type = default;
        var name = Enum.GetNames<DiscountType>().FirstOrDefault(candidate => string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase));

        return name is not null && Enum.TryParse(name, out type);
    }
}

internal static class DiscountRuleVersions
{
    /// <summary>
    /// The DTO of version <paramref name="version"/> of a rule built from <paramref name="definition"/>. The Domain builds
    /// the rule too, so what is stored is exactly what the engine will accept when it reads it back.
    /// </summary>
    public static Result<DiscountRuleDto> Build(
        Guid id, int version, RuleDefinition definition, string author, DateTimeOffset now)
    {
        var validFrom = definition.ValidFrom ?? now;
        var conditions = definition.Conditions ?? RuleDefinitionValidator.NoConditions;

        if (!RuleTypes.TryParse(definition.Type, out var type))
        {
            return Result.Fail<DiscountRuleDto>(ErrorKind.Validation, $"Unknown discount type '{definition.Type}'.");
        }

        try
        {
            var rule = DiscountRule.Create(
                id, version, definition.Name, type, definition.Parameters, conditions, definition.Priority, definition.Stackable,
                definition.OncePerClient, validFrom, definition.ValidTo, definition.IsActive);

            return Result.Ok(new DiscountRuleDto(
                rule.Id,
                rule.Version,
                rule.Name,
                rule.Type.ToString(),
                Parse(definition.Parameters!),
                Parse(conditions),
                rule.Priority,
                rule.Stackable,
                rule.OncePerClient,
                rule.ValidFrom,
                rule.ValidTo,
                rule.IsActive,
                author,
                now));
        }
        catch (DomainException exception)
        {
            return Result.Fail<DiscountRuleDto>(ErrorKind.Validation, exception.Message);
        }
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }
}
