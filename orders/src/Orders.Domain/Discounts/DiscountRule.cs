using FluentValidation;
using Orders.Domain.Rules;
using Orders.Domain.Validation;

namespace Orders.Domain.Discounts;

/// <summary>
/// One version of an admin-managed discount rule. Every edit is a new version; an order keeps the id and version it
/// used, so a rule can change without changing past orders.
/// </summary>
public sealed class DiscountRule
{
    private static readonly InputValidator _validator = new();

    private DiscountRule(
        Guid id,
        int version,
        string name,
        IDiscountStrategy strategy,
        RuleConditions conditions,
        int priority,
        bool stackable,
        bool oncePerClient,
        DateTimeOffset validFrom,
        DateTimeOffset? validTo,
        bool isActive)
    {
        Id = id;
        Version = version;
        Name = name;
        Strategy = strategy;
        Conditions = conditions;
        Priority = priority;
        Stackable = stackable;
        OncePerClient = oncePerClient;
        ValidFrom = validFrom;
        ValidTo = validTo;
        IsActive = isActive;
    }

    public Guid Id { get; }

    public int Version { get; }

    public string Name { get; }

    public DiscountType Type => Strategy.Type;

    public IDiscountStrategy Strategy { get; }

    public RuleConditions Conditions { get; }

    /// <summary>Lower runs first.</summary>
    public int Priority { get; }

    public bool Stackable { get; }

    public bool OncePerClient { get; }

    public DateTimeOffset ValidFrom { get; }

    /// <summary>Exclusive; null means no end.</summary>
    public DateTimeOffset? ValidTo { get; }

    public bool IsActive { get; }

    /// <summary>Builds a rule from its stored or submitted form; throws <see cref="DomainException"/> on any invalid part.</summary>
    public static DiscountRule Create(
        Guid id,
        int version,
        string? name,
        DiscountType type,
        string? parameters,
        string? conditions,
        int priority,
        bool stackable,
        bool oncePerClient,
        DateTimeOffset validFrom,
        DateTimeOffset? validTo,
        bool isActive)
    {
        var trimmedName = name?.Trim();
        _validator.EnsureValid(new Input(id, version, trimmedName, validFrom, validTo));

        return new DiscountRule(
            id,
            version,
            trimmedName!,
            DiscountStrategies.Create(type, parameters),
            RuleConditions.Create(conditions),
            priority,
            stackable,
            oncePerClient,
            validFrom,
            validTo,
            isActive);
    }

    /// <summary>
    /// False when the rule can never take anything off a cart in <paramref name="currency"/>: a fixed amount or tiers
    /// in other currencies only, or a <c>MinSubtotal</c> condition in another currency.
    /// </summary>
    public bool CanApplyIn(Currency currency)
    {
        var strategyFits = Strategy switch
        {
            FixedAmountDiscount fixedAmount => fixedAmount.Parameters.Amounts.Any(amount => Currency.Create(amount.Currency) == currency),
            TieredDiscount tiered => Currency.Create(tiered.Parameters.Currency) == currency,
            _ => true,
        };

        return strategyFits && Conditions.Items.OfType<MinSubtotalCondition>().All(condition => Currency.Create(condition.Currency) == currency);
    }

    /// <summary>Active and within its validity window at <paramref name="now"/>.</summary>
    public bool IsInEffectAt(DateTimeOffset now) => IsActive && ValidFrom <= now && (ValidTo is null || now < ValidTo);

    private sealed record Input(Guid Id, int Version, string? Name, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo);

    private sealed class InputValidator : AbstractValidator<Input>
    {
        public InputValidator()
        {
            RuleFor(input => input.Id).NotEqual(Guid.Empty).WithMessage("Rule id must not be empty.");
            RuleFor(input => input.Version).GreaterThanOrEqualTo(1).WithMessage("Rule version starts at 1.");
            RuleFor(input => input.Name).NotEmpty().WithMessage("Rule name must not be empty.");
            RuleFor(input => input.ValidTo)
                .GreaterThan(input => input.ValidFrom)
                .When(input => input.ValidTo is not null)
                .WithMessage("validTo must be later than validFrom.");
        }
    }
}
