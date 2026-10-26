using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.Results;
using Orders.Domain.Validation;

namespace Orders.Domain.Rules;

/// <summary>
/// One condition of a rule, stored as JSON with its <c>kind</c>. Discount rules use the first six kinds; confirmation
/// rules (Phase 4) also use <c>TotalAbove</c>, <c>FirstOrderOfClient</c> and <c>ChannelIs</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(MinSubtotalCondition), "MinSubtotal")]
[JsonDerivedType(typeof(SkuInCondition), "SkuIn")]
[JsonDerivedType(typeof(CategoryInCondition), "CategoryIn")]
[JsonDerivedType(typeof(MinQuantityCondition), "MinQuantity")]
[JsonDerivedType(typeof(ClientSegmentCondition), "ClientSegment")]
[JsonDerivedType(typeof(DateWindowCondition), "DateWindow")]
[JsonDerivedType(typeof(TotalAboveCondition), "TotalAbove")]
[JsonDerivedType(typeof(FirstOrderOfClientCondition), "FirstOrderOfClient")]
[JsonDerivedType(typeof(ChannelIsCondition), "ChannelIs")]
public abstract record RuleCondition : ISpecification<RuleContext>
{
    public abstract bool IsSatisfiedBy(RuleContext candidate);
}

/// <summary>The cart subtotal is at least the amount, in that currency; a cart in another currency does not match.</summary>
public sealed record MinSubtotalCondition(decimal Amount, string Currency) : RuleCondition
{
    public override bool IsSatisfiedBy(RuleContext candidate) =>
        candidate.Cart.Currency == Domain.Currency.Create(Currency) && candidate.Cart.Subtotal.Amount >= Amount;
}

/// <summary>The cart holds at least one of the SKUs.</summary>
public sealed record SkuInCondition(IReadOnlyList<string> Skus) : RuleCondition
{
    public override bool IsSatisfiedBy(RuleContext candidate)
    {
        var skus = Skus.Select(sku => sku.Trim().ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);

        return candidate.Cart.Lines.Any(line => skus.Contains(line.Sku));
    }
}

/// <summary>The cart holds at least one product of the categories (case-insensitive).</summary>
public sealed record CategoryInCondition(IReadOnlyList<string> Categories) : RuleCondition
{
    public override bool IsSatisfiedBy(RuleContext candidate)
    {
        var categories = Categories.Select(category => category.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidate.Cart.Lines.Any(line => categories.Contains(line.Category));
    }
}

/// <summary>The cart holds at least that many units, of the SKUs when listed.</summary>
public sealed record MinQuantityCondition(int Quantity, IReadOnlyList<string>? Skus = null) : RuleCondition
{
    public override bool IsSatisfiedBy(RuleContext candidate)
    {
        var skus = Skus?.Select(sku => sku.Trim().ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);

        return candidate.Cart.Lines.Where(line => skus is null || skus.Contains(line.Sku)).Sum(line => (long)line.Quantity) >= Quantity;
    }
}

/// <summary>The client belongs to at least one of the segments (case-insensitive).</summary>
public sealed record ClientSegmentCondition(IReadOnlyList<string> Segments) : RuleCondition
{
    public override bool IsSatisfiedBy(RuleContext candidate)
    {
        var segments = Segments.Select(segment => segment.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidate.ClientSegments.Any(segments.Contains);
    }
}

/// <summary>Now is within <c>[from, to)</c>; either end may be open, not both.</summary>
public sealed record DateWindowCondition(DateTimeOffset? From = null, DateTimeOffset? To = null) : RuleCondition
{
    public override bool IsSatisfiedBy(RuleContext candidate) =>
        (From is null || candidate.Now >= From) && (To is null || candidate.Now < To);
}

/// <summary>The context total is strictly above the amount, in that currency; another currency does not match.</summary>
public sealed record TotalAboveCondition(decimal Amount, string Currency) : RuleCondition
{
    public override bool IsSatisfiedBy(RuleContext candidate) =>
        candidate.Total.Currency == Domain.Currency.Create(Currency) && candidate.Total.Amount > Amount;
}

/// <summary>The client has no earlier order.</summary>
public sealed record FirstOrderOfClientCondition : RuleCondition
{
    public override bool IsSatisfiedBy(RuleContext candidate) => candidate.IsFirstOrderOfClient;
}

public sealed record ChannelIsCondition(OrderChannel Channel) : RuleCondition
{
    public override bool IsSatisfiedBy(RuleContext candidate) => candidate.Channel == Channel;
}

/// <summary>A rule's conditions, combined with AND, read from its <c>conditions</c> JSON array.</summary>
public sealed class RuleConditions : ISpecification<RuleContext>
{
    private static readonly ListValidator _validator = new();

    private readonly AllOf<RuleContext> _all;

    private RuleConditions(IReadOnlyList<RuleCondition> items)
    {
        Items = items;
        _all = new AllOf<RuleContext>(items);
    }

    public static RuleConditions None { get; } = new([]);

    public IReadOnlyList<RuleCondition> Items { get; }

    public static ValidationResult Validate(string? conditions) => Parse(conditions).Validation;

    /// <summary>Throws <see cref="DomainException"/> when the JSON is not a valid list of known conditions.</summary>
    public static RuleConditions Create(string? conditions)
    {
        var (parsed, validation) = Parse(conditions);

        return validation.IsValid ? parsed! : throw new DomainException(validation.ToString(" "));
    }

    public bool IsSatisfiedBy(RuleContext candidate) => _all.IsSatisfiedBy(candidate);

    private static (RuleConditions? Conditions, ValidationResult Validation) Parse(string? conditions) =>
        StrictJson.Parse<List<RuleCondition>, RuleConditions>(conditions, _validator, items => new RuleConditions(items), "conditions");

    private sealed class ListValidator : AbstractValidator<List<RuleCondition>>
    {
        public ListValidator()
        {
            RuleForEach(items => items)
                .NotNull().WithMessage("A condition must not be null.")
                .SetInheritanceValidator(validators => validators
                    .Add(new MoneyThresholdValidator<MinSubtotalCondition>(condition => condition.Amount, condition => condition.Currency))
                    .Add(new MoneyThresholdValidator<TotalAboveCondition>(condition => condition.Amount, condition => condition.Currency))
                    .Add(new TextListValidator<SkuInCondition>(condition => condition.Skus, "skus"))
                    .Add(new TextListValidator<CategoryInCondition>(condition => condition.Categories, "categories"))
                    .Add(new TextListValidator<ClientSegmentCondition>(condition => condition.Segments, "segments"))
                    .Add(new MinQuantityValidator())
                    .Add(new DateWindowValidator())
                    .Add(new ChannelValidator()));
        }
    }

    private sealed class MoneyThresholdValidator<T> : AbstractValidator<T>
    {
        public MoneyThresholdValidator(Func<T, decimal> amount, Func<T, string> currency)
        {
            RuleFor(condition => amount(condition)).GreaterThanOrEqualTo(0m).WithMessage("amount must not be negative.").OverridePropertyName("amount");
            RuleFor(condition => currency(condition))
                .Must(code => Currency.TryCreate(code, out _))
                .WithMessage("Currency '{PropertyValue}' must be a three-letter ISO 4217 code.")
                .OverridePropertyName("currency");
        }
    }

    private sealed class TextListValidator<T> : AbstractValidator<T>
    {
        public TextListValidator(Func<T, IReadOnlyList<string>> values, string name)
        {
            RuleFor(condition => values(condition))
                .Must(list => list.Count > 0 && list.All(value => !string.IsNullOrWhiteSpace(value)))
                .WithMessage($"{name} lists at least one non-empty value.")
                .OverridePropertyName(name);
        }
    }

    private sealed class MinQuantityValidator : AbstractValidator<MinQuantityCondition>
    {
        public MinQuantityValidator()
        {
            RuleFor(condition => condition.Quantity).GreaterThanOrEqualTo(1).WithMessage("quantity must be at least 1.");
            RuleFor(condition => condition.Skus)
                .Must(skus => skus is null || (skus.Count > 0 && skus.All(sku => !string.IsNullOrWhiteSpace(sku))))
                .WithMessage("skus, when present, lists at least one non-empty SKU.");
        }
    }

    private sealed class DateWindowValidator : AbstractValidator<DateWindowCondition>
    {
        public DateWindowValidator()
        {
            RuleFor(condition => condition)
                .Must(condition => condition.From is not null || condition.To is not null)
                .WithMessage("A date window needs from, to or both.")
                .Must(condition => condition.From is null || condition.To is null || condition.From < condition.To)
                .WithMessage("from must be earlier than to.")
                .OverridePropertyName("dateWindow");
        }
    }

    private sealed class ChannelValidator : AbstractValidator<ChannelIsCondition>
    {
        public ChannelValidator()
        {
            RuleFor(condition => condition.Channel).IsInEnum();
        }
    }
}
