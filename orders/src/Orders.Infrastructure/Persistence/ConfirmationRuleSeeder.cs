using Microsoft.EntityFrameworkCore;
using Orders.Domain.Confirmation;

namespace Orders.Infrastructure.Persistence;

public sealed record ConfirmationOptions
{
    public const string SectionName = "Orders:Confirmation";

    /// <summary>
    /// Adds the development rule that holds BRL orders above 5,000.00 when the migrator runs. Off by default; only the
    /// development compose file turns it on, so staging and production never get it.
    /// </summary>
    public bool SeedEnabled { get; init; }

    /// <summary>
    /// How long the active confirmation rules stay cached in each process. An admin write empties only the cache of the API
    /// that handled it, so the Worker, which evaluates the rules, uses a change after at most this long.
    /// </summary>
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromSeconds(5);
}

internal static class ConfirmationRuleSeeder
{
    /// <summary>A fixed id, so the seed is added once however often the migrator runs.</summary>
    public static readonly Guid LargeOrderRuleId = Guid.Parse("c0ffee00-5eed-4a11-8000-000000000001");

    private const string _largeBrlOrders = """[{ "kind": "TotalAbove", "amount": 5000, "currency": "BRL" }]""";

    public static async Task SeedAsync(OrdersDbContext context, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await context.ConfirmationRules.AnyAsync(rule => rule.Id == LargeOrderRuleId, cancellationToken))
        {
            return;
        }

        // Built through the Domain, so the seed is a rule the Worker accepts.
        var rule = ConfirmationRule.Create(LargeOrderRuleId, 1, "Large BRL order", ConfirmationAction.Hold, _largeBrlOrders, 100, true);
        context.ConfirmationRules.Add(new ConfirmationRuleRecord
        {
            Id = rule.Id,
            Version = rule.Version,
            Name = rule.Name,
            Action = rule.Action,
            Conditions = _largeBrlOrders,
            Priority = rule.Priority,
            IsActive = rule.IsActive,
            CreatedBy = "seed",
            CreatedAt = clock.GetUtcNow(),
        });
        await context.SaveChangesAsync(cancellationToken);
    }
}
