using Orders.Application.Discounts;
using Orders.Application.Pricing;
using Orders.Domain;
using Orders.Domain.Discounts;

namespace Orders.Application.Ordering;

/// <summary>An unsaved order and the discount rules it used.</summary>
public sealed record OrderDraft(Order Order, IReadOnlyList<AppliedDiscount> Discounts);

/// <summary>
/// The one pricing and discount path shared by order creation and quotes: prices the cart with one Catalog call,
/// runs the discount engine over the active rules and builds the order it would place. The draft is not saved;
/// creation stores it, a quote only reads its totals.
/// </summary>
public sealed class OrderDrafter(
    PricingService pricing,
    IActiveDiscountRules discountRules,
    IOrderQueries orders,
    DiscountEngine engine,
    TimeProvider clock)
{
    public const string DefaultCurrency = "BRL";

    private static readonly IReadOnlySet<string> _noSegments = new HashSet<string>();

    /// <param name="currencyCode">ISO 4217 code, already validated by the caller; BRL when null.</param>
    public async Task<Result<OrderDraft>> DraftAsync(
        Guid clientId,
        string clientName,
        string createdBy,
        OrderChannel channel,
        string? currencyCode,
        IReadOnlyList<CartLine> lines,
        CancellationToken cancellationToken)
    {
        try
        {
            var currency = Currency.Create(currencyCode ?? DefaultCurrency);
            var client = ClientRef.Create(clientId, clientName);
            var priced = await pricing.PriceAsync(currency, lines, cancellationToken);

            if (!priced.IsSuccess)
            {
                return Result.Fail<OrderDraft>(priced.Error!.Kind, priced.Error.Message);
            }

            var discounts = await DiscountAsync(priced.Value, clientId, channel, cancellationToken);
            var now = clock.GetUtcNow();
            var orderId = Guid.NewGuid();
            var order = Order.Place(
                orderId,
                OrderNumbers.For(orderId, now),
                client,
                createdBy,
                channel,
                priced.Value.Lines.Select(line => line.ToOrderItem(discounts.LineDiscountFor(line.Sku))),
                now);

            return Result.Ok(new OrderDraft(order, discounts.Applied));
        }
        catch (DomainException exception)
        {
            return Result.Fail<OrderDraft>(ErrorKind.Validation, exception.Message);
        }
    }

    private async Task<DiscountOutcome> DiscountAsync(PricedCart priced, Guid clientId, OrderChannel channel, CancellationToken cancellationToken)
    {
        var rules = await discountRules.GetAsync(priced.Currency, cancellationToken);

        if (rules.Count == 0)
        {
            return new DiscountOutcome(Money.Zero(priced.Currency), [], []);
        }

        // A once-per-client rule the client already redeemed is left out, so the order is priced without it instead of
        // being priced with it and then refused when the redemption is stored.
        var redeemed = await orders.RedeemedOncePerClientRuleIdsAsync(clientId, cancellationToken);
        var offered = redeemed.Count == 0
            ? rules
            : rules.Where(rule => !(rule.OncePerClient && redeemed.Contains(rule.Id))).ToList();

        var cart = DiscountCart.Create(
            priced.Currency, priced.Lines.Select(line => new DiscountLine(line.Sku, line.Category, line.UnitPrice, line.Quantity)));

        // No segment source exists yet, so ClientSegment conditions never match.
        var client = new DiscountClient(clientId, _noSegments, channel, IsFirstOrder: !await orders.ClientHasOrdersAsync(clientId, cancellationToken));

        return engine.Evaluate(offered, cart, client);
    }
}
