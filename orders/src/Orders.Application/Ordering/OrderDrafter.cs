using Orders.Application.Pricing;
using Orders.Domain;

namespace Orders.Application.Ordering;

/// <summary>
/// The one pricing path shared by order creation and quotes: prices the cart with one Catalog call and builds the
/// order it would place. The draft is not saved; creation stores it, a quote only reads its totals.
/// </summary>
public sealed class OrderDrafter(PricingService pricing, TimeProvider clock)
{
    public const string DefaultCurrency = "BRL";

    /// <param name="currencyCode">ISO 4217 code, already validated by the caller; BRL when null.</param>
    public async Task<Result<Order>> DraftAsync(
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
                return Result.Fail<Order>(priced.Error!.Kind, priced.Error.Message);
            }

            var now = clock.GetUtcNow();
            var orderId = Guid.NewGuid();

            // No discounts until the discount engine arrives (C27-C29): every line keeps its full price.
            return Result.Ok(Order.Place(
                orderId,
                OrderNumbers.For(orderId, now),
                client,
                createdBy,
                channel,
                priced.Value.Lines.Select(line => line.ToOrderItem()),
                now));
        }
        catch (DomainException exception)
        {
            return Result.Fail<Order>(ErrorKind.Validation, exception.Message);
        }
    }
}
