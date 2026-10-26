using Orders.Application.Discounts;
using Orders.Application.Pricing;
using Orders.Domain;
using Orders.Domain.Discounts;

namespace Orders.Application.Ordering;

/// <summary>An unsaved order and the discount rules it used.</summary>
/// <param name="AssumedFirstOrder">The client had no orders when the draft was priced, so first-order rules were applied.</param>
public sealed record OrderDraft(Order Order, IReadOnlyList<AppliedDiscount> Discounts, bool AssumedFirstOrder = false);

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
                priced.Value.Lines.Select(line => line.ToOrderItem(discounts.Outcome.LineDiscountFor(line.Sku))),
                now);

            return Result.Ok(new OrderDraft(order, discounts.Outcome.Applied, discounts.AssumedFirstOrder));
        }
        catch (DomainException exception)
        {
            return Result.Fail<OrderDraft>(ErrorKind.Validation, exception.Message);
        }
    }

    private async Task<Discounted> DiscountAsync(PricedCart priced, Guid clientId, OrderChannel channel, CancellationToken cancellationToken)
    {
        var rules = await discountRules.GetAsync(priced.Currency, cancellationToken);

        if (rules.Count == 0)
        {
            return new Discounted(new DiscountOutcome(Money.Zero(priced.Currency), [], []), AssumedFirstOrder: false);
        }

        var cart = DiscountCart.Create(
            priced.Currency, priced.Lines.Select(line => new DiscountLine(line.Sku, line.Category, line.UnitPrice, line.Quantity)));

        // No segment source exists yet, so ClientSegment conditions never match.
        var isFirstOrder = !await orders.ClientHasOrdersAsync(clientId, cancellationToken);
        var client = new DiscountClient(clientId, _noSegments, channel, isFirstOrder);

        return new Discounted(engine.Evaluate(rules, cart, client), isFirstOrder);
    }

    private sealed record Discounted(DiscountOutcome Outcome, bool AssumedFirstOrder);
}
