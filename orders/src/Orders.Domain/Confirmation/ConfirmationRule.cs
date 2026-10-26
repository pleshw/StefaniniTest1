using FluentValidation;
using FluentValidation.Results;
using Orders.Domain.Rules;
using Orders.Domain.Validation;

namespace Orders.Domain.Confirmation;

/// <summary>What a matching confirmation rule does to a new order.</summary>
public enum ConfirmationAction
{
    /// <summary>The order goes to OnHold and waits for an admin.</summary>
    Hold,
}

/// <summary>
/// One version of an admin-managed confirmation rule. The Worker looks at every new order: if an active Hold rule matches
/// it, the order goes to OnHold (the reason is the rule name), otherwise it is confirmed. Conditions are the rule
/// specifications of the discount engine, but only the kinds that need nothing but the order itself: <c>TotalAbove</c>,
/// <c>FirstOrderOfClient</c> and <c>ChannelIs</c>.
/// </summary>
public sealed class ConfirmationRule
{
    private static readonly InputValidator _validator = new();

    private ConfirmationRule(Guid id, int version, string name, ConfirmationAction action, RuleConditions conditions, int priority, bool isActive)
    {
        Id = id;
        Version = version;
        Name = name;
        Action = action;
        Conditions = conditions;
        Priority = priority;
        IsActive = isActive;
    }

    public Guid Id { get; }

    public int Version { get; }

    public string Name { get; }

    public ConfirmationAction Action { get; }

    public RuleConditions Conditions { get; }

    /// <summary>Lower runs first.</summary>
    public int Priority { get; }

    public bool IsActive { get; }

    /// <summary>Builds a rule from its stored or submitted form; throws <see cref="DomainException"/> on any invalid part.</summary>
    public static ConfirmationRule Create(
        Guid id, int version, string? name, ConfirmationAction action, string? conditions, int priority, bool isActive)
    {
        var trimmedName = name?.Trim();
        _validator.EnsureValid(new Input(id, version, trimmedName, action));
        var validation = ValidateConditions(conditions);

        return validation.IsValid
            ? new ConfirmationRule(id, version, trimmedName!, action, RuleConditions.Create(conditions), priority, isActive)
            : throw new DomainException(validation.ToString(" "));
    }

    /// <summary>The conditions must be valid, hold at least one condition and use only the kinds confirmation understands.</summary>
    public static ValidationResult ValidateConditions(string? conditions)
    {
        var validation = RuleConditions.Validate(conditions);

        if (!validation.IsValid)
        {
            return validation;
        }

        var parsed = RuleConditions.Create(conditions);

        if (parsed.Items.Count == 0)
        {
            return StrictJson.Failure("conditions", "A confirmation rule needs at least one condition.");
        }

        var unsupported = parsed.Items.Where(item => item is not (TotalAboveCondition or FirstOrderOfClientCondition or ChannelIsCondition)).ToList();

        return unsupported.Count == 0
            ? validation
            : StrictJson.Failure(
                "conditions",
                $"Confirmation rules support only TotalAbove, FirstOrderOfClient and ChannelIs, not {string.Join(", ", unsupported.Select(item => item.GetType().Name.Replace("Condition", string.Empty, StringComparison.Ordinal)).Distinct())}.");
    }

    /// <summary>Active and every condition holds for <paramref name="context"/>.</summary>
    public bool Matches(RuleContext context) => IsActive && Conditions.IsSatisfiedBy(context);

    /// <summary>The Hold rule that decides: among the matching ones the lowest priority, then the lowest id; null when none matches.</summary>
    public static ConfirmationRule? FirstMatchingHold(IEnumerable<ConfirmationRule> rules, RuleContext context) =>
        rules
            .Where(rule => rule.Action == ConfirmationAction.Hold && rule.Matches(context))
            .OrderBy(rule => rule.Priority)
            .ThenBy(rule => rule.Id)
            .FirstOrDefault();

    private sealed record Input(Guid Id, int Version, string? Name, ConfirmationAction Action);

    private sealed class InputValidator : AbstractValidator<Input>
    {
        public InputValidator()
        {
            RuleFor(input => input.Id).NotEqual(Guid.Empty).WithMessage("Rule id must not be empty.");
            RuleFor(input => input.Version).GreaterThanOrEqualTo(1).WithMessage("Rule version starts at 1.");
            RuleFor(input => input.Name).NotEmpty().WithMessage("Rule name must not be empty.");
            RuleFor(input => input.Action).IsInEnum().WithMessage("Unknown confirmation action.");
        }
    }
}
