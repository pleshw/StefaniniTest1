using Orders.Domain;
using Orders.Domain.Discounts;

namespace Orders.Application.Discounts;

/// <summary>Stored discount rules. Each rule id has versions; only the latest version of a rule counts.</summary>
public interface IDiscountRuleRepository
{
    /// <summary>The latest version of every rule, when that version is active, lowest priority first.</summary>
    Task<IReadOnlyList<DiscountRule>> GetActiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The active rules, cached for a short time under one key. Every admin write calls <see cref="Invalidate"/>, so the
/// next order or quote sees the change at once.
/// </summary>
public interface IActiveDiscountRules
{
    public const string CacheKey = "discount-rules:active";

    /// <summary>The active rules that can take something off a cart in <paramref name="currency"/>.</summary>
    Task<IReadOnlyList<DiscountRule>> GetAsync(Currency currency, CancellationToken cancellationToken);

    void Invalidate();
}
