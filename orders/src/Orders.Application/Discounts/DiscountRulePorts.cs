using Orders.Domain;
using Orders.Domain.Discounts;

namespace Orders.Application.Discounts;

/// <summary>Stored discount rules. Each rule id has versions; only the latest version of a rule counts.</summary>
public interface IDiscountRuleRepository
{
    /// <summary>The latest version of every rule, when that version is active, lowest priority first.</summary>
    Task<IReadOnlyList<DiscountRule>> GetActiveAsync(CancellationToken cancellationToken);

    /// <summary>The latest version of every rule, active or not, lowest priority first; <paramref name="active"/> filters on that version.</summary>
    Task<IReadOnlyList<DiscountRuleDto>> ListLatestAsync(bool? active, CancellationToken cancellationToken);

    Task<DiscountRuleDto?> GetLatestAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Stores a new version. False when that (id, version) exists already: a concurrent edit took it.</summary>
    Task<bool> AddVersionAsync(DiscountRuleDto rule, CancellationToken cancellationToken);
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

/// <summary>
/// Stores the discounts an order used, inside the order transaction: one <c>order_discounts</c> row and one
/// <c>discount_redemptions</c> row per applied rule.
/// </summary>
public interface IOrderDiscountStore
{
    /// <summary>Throws <see cref="DiscountAlreadyUsedException"/> when the client already redeemed a once-per-client rule.</summary>
    Task AddAsync(Order order, IReadOnlyList<AppliedDiscount> discounts, CancellationToken cancellationToken);
}

/// <summary>A once-per-client rule was redeemed by this client already, possibly by a concurrent order.</summary>
public sealed class DiscountAlreadyUsedException(string message, Exception innerException) : Exception(message, innerException);

public sealed record DiscountOptions
{
    public const string SectionName = "Orders:Discounts";

    /// <summary>How long the active rules stay cached; admin writes invalidate them sooner.</summary>
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The most the engine takes off an order, as a percentage of its subtotal.</summary>
    public decimal MaxDiscountPercent { get; init; } = DiscountEngine.DefaultMaxDiscountPercent;
}
