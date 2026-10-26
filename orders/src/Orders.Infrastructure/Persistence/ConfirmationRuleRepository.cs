using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Orders.Application.Confirmation;
using Orders.Domain.Confirmation;
using Riok.Mapperly.Abstractions;

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

    public async Task<IReadOnlyList<ConfirmationRuleDto>> ListLatestAsync(bool? active, CancellationToken cancellationToken)
    {
        var records = await context.ConfirmationRules
            .AsNoTracking()
            .Where(rule => !context.ConfirmationRules.Any(newer => newer.Id == rule.Id && newer.Version > rule.Version))
            .Where(rule => active == null || rule.IsActive == active)
            .OrderBy(rule => rule.Priority)
            .ThenBy(rule => rule.Id)
            .ToListAsync(cancellationToken);

        return records.Select(ConfirmationRuleDtoMapper.ToDto).ToList();
    }

    public async Task<ConfirmationRuleDto?> GetLatestAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await context.ConfirmationRules
            .AsNoTracking()
            .Where(rule => rule.Id == id)
            .OrderByDescending(rule => rule.Version)
            .FirstOrDefaultAsync(cancellationToken);

        return record is null ? null : ConfirmationRuleDtoMapper.ToDto(record);
    }

    public async Task<bool> AddVersionAsync(ConfirmationRuleDto rule, CancellationToken cancellationToken)
    {
        var record = ConfirmationRuleDtoMapper.ToRecord(rule);
        context.ConfirmationRules.Add(record);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            context.Entry(record).State = EntityState.Detached;
            return false;
        }
    }
}

/// <summary>Maps stored rule versions to and from the admin API shape; conditions stay JSON.</summary>
[Mapper]
internal static partial class ConfirmationRuleDtoMapper
{
    public static partial ConfirmationRuleDto ToDto(ConfirmationRuleRecord record);

    public static partial ConfirmationRuleRecord ToRecord(ConfirmationRuleDto rule);

    private static JsonElement ToElement(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    private static string ToJson(JsonElement element) => element.GetRawText();
}

/// <summary>
/// Every active rule under one cache entry, <see cref="IActiveConfirmationRules.CacheKey"/>. The cache is per instance:
/// an admin write empties the entry of the API instance that handled it, any other instance (the Worker) sees it when its
/// own entry expires after <see cref="ConfirmationOptions.CacheDuration"/>.
/// </summary>
internal sealed class CachedActiveConfirmationRules(IConfirmationRuleRepository repository, IMemoryCache cache, ConfirmationOptions options)
    : IActiveConfirmationRules
{
    public async Task<IReadOnlyList<ConfirmationRule>> GetAsync(CancellationToken cancellationToken)
    {
        var rules = await cache.GetOrCreateAsync(
            IActiveConfirmationRules.CacheKey,
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = options.CacheDuration;
                return await repository.GetActiveAsync(cancellationToken);
            });

        return rules!;
    }

    public void Invalidate() => cache.Remove(IActiveConfirmationRules.CacheKey);
}

internal static class ConfirmationRuleMapper
{
    /// <summary>Rules are rebuilt through <see cref="ConfirmationRule.Create"/>, so a stored rule is validated on every read.</summary>
    public static ConfirmationRule ToDomain(ConfirmationRuleRecord record) => ConfirmationRule.Create(
        record.Id, record.Version, record.Name, record.Action, record.Conditions, record.Priority, record.IsActive);
}
