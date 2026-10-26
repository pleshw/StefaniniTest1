using FluentValidation;
using Orders.Domain.Rules;
using Orders.Domain.Validation;

namespace Orders.Domain.Discounts;

/// <summary>Who orders, as the rule conditions see it.</summary>
/// <param name="Segments">Segments the client belongs to; empty while no segment source exists.</param>
public sealed record DiscountClient(Guid Id, IReadOnlySet<string> Segments, OrderChannel Channel, bool IsFirstOrder);

/// <summary>One rule the engine applied, with the version an order keeps.</summary>
public sealed record AppliedDiscount(Guid RuleId, int RuleVersion, Money Amount, bool OncePerClient);

/// <summary>What the engine takes off a cart: per rule, and spread over the lines (they add up to the total).</summary>
public sealed record DiscountOutcome(Money Total, IReadOnlyList<AppliedDiscount> Applied, IReadOnlyList<DiscountLineAmount> Lines)
{
    public Money LineDiscountFor(string sku) =>
        Lines.FirstOrDefault(line => line.Sku == sku)?.Amount ?? Money.Zero(Total.Currency);
}

/// <summary>
/// Pure, deterministic discount evaluation. Rules in effect whose conditions hold are computed on the cart; the best
/// non-stackable rule applies first (largest amount, then lower priority, then lower id), then every stackable rule by
/// ascending priority (then id). A line never gets more off than it costs, and the total never exceeds
/// <c>maxDiscountPercent</c> of the subtotal: the rule that reaches the cap is cut, later ones get nothing.
/// Order-level amounts are spread over the lines in proportion to what is left of them, to the cent.
/// </summary>
public sealed class DiscountEngine
{
    public const decimal DefaultMaxDiscountPercent = 50m;

    private const int _centsPerUnit = 100;

    private static readonly CapValidator _capValidator = new();

    private readonly TimeProvider _clock;
    private readonly decimal _maxDiscountPercent;

    public DiscountEngine(TimeProvider clock, decimal maxDiscountPercent = DefaultMaxDiscountPercent)
    {
        _capValidator.EnsureValid(maxDiscountPercent);
        _clock = clock;
        _maxDiscountPercent = maxDiscountPercent;
    }

    public DiscountOutcome Evaluate(IEnumerable<DiscountRule> rules, DiscountCart cart, DiscountClient client)
    {
        var context = new RuleContext(cart, client.Id, client.Segments, client.Channel, client.IsFirstOrder, cart.Subtotal, _clock.GetUtcNow());
        var candidates = rules
            .Where(rule => rule.IsInEffectAt(context.Now) && rule.Conditions.IsSatisfiedBy(context))
            .Select(rule => (Rule: rule, Result: rule.Strategy.Calculate(cart)))
            .Where(candidate => !candidate.Result.IsZero)
            .ToList();

        var best = candidates
            .Where(candidate => !candidate.Rule.Stackable)
            .OrderByDescending(candidate => candidate.Result.Total.Amount)
            .ThenBy(candidate => candidate.Rule.Priority)
            .ThenBy(candidate => candidate.Rule.Id)
            .Take(1);
        var stackable = candidates
            .Where(candidate => candidate.Rule.Stackable)
            .OrderBy(candidate => candidate.Rule.Priority)
            .ThenBy(candidate => candidate.Rule.Id);

        return Apply(cart, best.Concat(stackable));
    }

    private DiscountOutcome Apply(DiscountCart cart, IEnumerable<(DiscountRule Rule, DiscountResult Result)> ordered)
    {
        var remaining = cart.Lines.Select(line => ToCents(line.LineSubtotal)).ToArray();
        var taken = new long[remaining.Length];
        var headroom = ToCents(cart.Subtotal.Percent(_maxDiscountPercent));
        var applied = new List<AppliedDiscount>();

        foreach (var (rule, result) in ordered)
        {
            var share = ShareOf(cart, result, remaining);
            var amount = share.Sum();

            if (amount > headroom)
            {
                share = Allocate(headroom, share);
                amount = headroom;
            }

            if (amount == 0)
            {
                continue;
            }

            for (var index = 0; index < share.Length; index++)
            {
                remaining[index] -= share[index];
                taken[index] += share[index];
            }

            headroom -= amount;
            applied.Add(new AppliedDiscount(rule.Id, rule.Version, FromCents(amount, cart.Currency), rule.OncePerClient));
        }

        var lines = cart.Lines
            .Select((line, index) => new DiscountLineAmount(line.Sku, FromCents(taken[index], cart.Currency)))
            .Where(line => line.Amount.Amount > 0m)
            .ToList();

        return new DiscountOutcome(FromCents(taken.Sum(), cart.Currency), applied, lines);
    }

    /// <summary>The cents this rule takes from each line, never more than what is left of the line.</summary>
    private static long[] ShareOf(DiscountCart cart, DiscountResult result, long[] remaining)
    {
        if (result.Lines.Count == 0)
        {
            return Allocate(Math.Min(ToCents(result.Total), remaining.Sum()), remaining);
        }

        return cart.Lines
            .Select((line, index) => Math.Min(
                remaining[index],
                result.Lines.Where(amount => amount.Sku == line.Sku).Sum(amount => ToCents(amount.Amount))))
            .ToArray();
    }

    /// <summary>
    /// Splits <paramref name="total"/> cents in proportion to <paramref name="weights"/> (largest remainder, ties to the
    /// first line), so the parts add up exactly. <paramref name="total"/> is at most the sum of the weights.
    /// </summary>
    private static long[] Allocate(long total, long[] weights)
    {
        var weightSum = weights.Sum();
        var parts = new long[weights.Length];

        if (total == 0 || weightSum == 0)
        {
            return parts;
        }

        var remainders = new decimal[weights.Length];

        for (var index = 0; index < weights.Length; index++)
        {
            var exact = (decimal)total * weights[index] / weightSum;
            parts[index] = (long)Math.Floor(exact);
            remainders[index] = exact - parts[index];
        }

        var left = total - parts.Sum();
        var order = Enumerable.Range(0, weights.Length).OrderByDescending(index => remainders[index]).ThenBy(index => index);

        foreach (var index in order.Take((int)left))
        {
            parts[index]++;
        }

        return parts;
    }

    private static long ToCents(Money money) => (long)(money.Amount * _centsPerUnit);

    private static Money FromCents(long cents, Currency currency) => Money.Create((decimal)cents / _centsPerUnit, currency);

    private sealed class CapValidator : AbstractValidator<decimal>
    {
        public CapValidator()
        {
            RuleFor(percent => percent)
                .InclusiveBetween(0m, DiscountRules.MaxPercent)
                .WithMessage($"MaxDiscountPercent must be between 0 and {DiscountRules.MaxPercent}.")
                .OverridePropertyName("MaxDiscountPercent");
        }
    }
}
