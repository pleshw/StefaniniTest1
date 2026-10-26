using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orders.Application.Discounts;
using Orders.Domain;
using Orders.Domain.Discounts;

namespace Orders.Infrastructure.Persistence;

internal sealed class DiscountRuleRepository(OrdersDbContext context) : IDiscountRuleRepository
{
    public async Task<IReadOnlyList<DiscountRule>> GetActiveAsync(CancellationToken cancellationToken)
    {
        // A newer version supersedes every older one, active or not; the primary key (id, version) serves NOT EXISTS.
        var records = await context.DiscountRules
            .AsNoTracking()
            .Where(rule => rule.IsActive && !context.DiscountRules.Any(newer => newer.Id == rule.Id && newer.Version > rule.Version))
            .OrderBy(rule => rule.Priority)
            .ThenBy(rule => rule.Id)
            .ToListAsync(cancellationToken);

        return records.Select(DiscountRuleMapper.ToDomain).ToList();
    }
}

internal static class DiscountRuleMapper
{
    /// <summary>Rules are rebuilt through <see cref="DiscountRule.Create"/>, so a stored rule is validated on every read.</summary>
    public static DiscountRule ToDomain(DiscountRuleRecord record) => DiscountRule.Create(
        record.Id,
        record.Version,
        record.Name,
        record.Type,
        record.Parameters,
        record.Conditions,
        record.Priority,
        record.Stackable,
        record.OncePerClient,
        record.ValidFrom,
        record.ValidTo,
        record.IsActive);
}

/// <summary>
/// Every active rule under one cache entry, <see cref="IActiveDiscountRules.CacheKey"/>; filtered by currency per call.
/// The cache is per instance: another instance sees an admin change when its own entry expires.
/// </summary>
internal sealed class CachedActiveDiscountRules(IDiscountRuleRepository repository, IMemoryCache cache, DiscountOptions options)
    : IActiveDiscountRules
{
    public async Task<IReadOnlyList<DiscountRule>> GetAsync(Currency currency, CancellationToken cancellationToken)
    {
        var rules = await cache.GetOrCreateAsync(
            IActiveDiscountRules.CacheKey,
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = options.CacheDuration;
                return await repository.GetActiveAsync(cancellationToken);
            });

        return rules!.Where(rule => rule.CanApplyIn(currency)).ToList();
    }

    public void Invalidate() => cache.Remove(IActiveDiscountRules.CacheKey);
}
