using Orders.Domain.Discounts;

namespace Orders.Domain.Rules;

/// <summary>What rule conditions look at: the priced cart, who orders and through which channel, and when.</summary>
/// <param name="ClientSegments">Segments the client belongs to; compared case-insensitively.</param>
/// <param name="Total">The amount <c>TotalAbove</c> compares: the subtotal for discounts, the order total for confirmation.</param>
/// <param name="Now">The current time, read from the caller's <see cref="TimeProvider"/>.</param>
public sealed record RuleContext(
    DiscountCart Cart,
    Guid ClientId,
    IReadOnlySet<string> ClientSegments,
    OrderChannel Channel,
    bool IsFirstOrderOfClient,
    Money Total,
    DateTimeOffset Now);
