using Microsoft.EntityFrameworkCore;
using Orders.Application.Confirmation;
using Orders.Domain.Confirmation;

namespace Orders.Infrastructure.Persistence;

internal sealed class ConfirmationRuleRepository(OrdersDbContext context) : IConfirmationRuleRepository
{
    public async Task<IReadOnlyList<ConfirmationRule>> GetActiveAsync(CancellationToken cancellationToken)
    {
        // A newer version supersedes every older one, active or not; the primary key (id, version) serves NOT EXISTS.
        var records = await context.ConfirmationRules
            .AsNoTracking()
            .Where(rule => rule.IsActive && !context.ConfirmationRules.Any(newer => newer.Id == rule.Id && newer.Version > rule.Version))
            .OrderBy(rule => rule.Priority)
            .ThenBy(rule => rule.Id)
            .ToListAsync(cancellationToken);

        return records.Select(ConfirmationRuleMapper.ToDomain).ToList();
    }
}

internal static class ConfirmationRuleMapper
{
    /// <summary>Rules are rebuilt through <see cref="ConfirmationRule.Create"/>, so a stored rule is validated on every read.</summary>
    public static ConfirmationRule ToDomain(ConfirmationRuleRecord record) => ConfirmationRule.Create(
        record.Id, record.Version, record.Name, record.Action, record.Conditions, record.Priority, record.IsActive);
}
