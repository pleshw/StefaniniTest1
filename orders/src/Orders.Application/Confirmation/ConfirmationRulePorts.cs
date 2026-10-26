using System.Text.Json;
using Orders.Domain.Confirmation;

namespace Orders.Application.Confirmation;

/// <summary>Stored confirmation rules. Each rule id has versions; only the latest version of a rule counts.</summary>
public interface IConfirmationRuleRepository
{
    /// <summary>The latest version of every rule, when that version is active, lowest priority first.</summary>
    Task<IReadOnlyList<ConfirmationRule>> GetActiveAsync(CancellationToken cancellationToken);

    /// <summary>The latest version of every rule, active or not, lowest priority first; <paramref name="active"/> filters on that version.</summary>
    Task<IReadOnlyList<ConfirmationRuleDto>> ListLatestAsync(bool? active, CancellationToken cancellationToken);

    Task<ConfirmationRuleDto?> GetLatestAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Stores a new version. False when that (id, version) exists already: a concurrent edit took it.</summary>
    Task<bool> AddVersionAsync(ConfirmationRuleDto rule, CancellationToken cancellationToken);
}

/// <summary>
/// The active rules, cached for a short time under one key. Every admin write calls <see cref="Invalidate"/>, which empties
/// the cache of this process only: another process (the Worker evaluates the rules, the API writes them) sees the change
/// when its own entry expires, so the cache duration is short.
/// </summary>
public interface IActiveConfirmationRules
{
    public const string CacheKey = "confirmation-rules:active";

    Task<IReadOnlyList<ConfirmationRule>> GetAsync(CancellationToken cancellationToken);

    void Invalidate();
}

/// <summary>One stored version of a confirmation rule as the admin API shows it. Conditions are the stored JSON.</summary>
/// <param name="Action">The <see cref="ConfirmationAction"/> name.</param>
public sealed record ConfirmationRuleDto(
    Guid Id,
    int Version,
    string Name,
    string Action,
    JsonElement Conditions,
    int Priority,
    bool IsActive,
    string CreatedBy,
    DateTimeOffset CreatedAt);
