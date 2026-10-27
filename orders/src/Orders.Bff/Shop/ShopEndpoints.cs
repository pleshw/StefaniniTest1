using System.Security.Claims;
using Orders.Bff.Clients;
using Orders.Bff.Clients.Catalog;
using Orders.Bff.Clients.Orders;
using Orders.Bff.Composition;
using Orders.Bff.Security;

namespace Orders.Bff.Shop;

/// <summary>
/// The Shop page's routes. Products need no session; the quote and the orders are the signed-in user's, priced and
/// stored by Orders. The currency is the one the page sends (the shopper's choice), BRL when it sends none.
/// </summary>
internal static class ShopEndpoints
{
    public const string DefaultCurrency = "BRL";

    private const string _ordersPath = "/api/v1/orders";
    private const string _shopOrdersPath = "/bff/shop/orders";

    public static IEndpointRouteBuilder MapShopEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var shop = endpoints.MapGroup("/bff/shop").WithTags("Shop");

        shop.MapGet("/products", ListProducts)
            .WithSummary("Products with their current price in the chosen currency (BRL by default), keyset paged.");

        var signedIn = shop.MapGroup(string.Empty).RequireAuthorization();

        signedIn.MapPost("/cart/quote", Quote)
            .WithSummary("Prices and discounts for the cart, computed by Orders; nothing is stored.");
        signedIn.MapPost("/orders", PlaceOrder)
            .WithSummary("Places the order; Idempotency-Key is forwarded unchanged and Orders' answer is returned as it is.");
        signedIn.MapGet("/orders", ListOrders)
            .WithSummary("The signed-in client's orders, newest first, keyset paged.");
        signedIn.MapGet("/orders/{id:guid}", GetOrder)
            .WithSummary("One of the signed-in client's orders.");

        return endpoints;
    }

    private static Task<IResult> ListProducts(
        ICatalogApiClient catalog, string? q, string? category, string? currency, string? after, int? limit, CancellationToken cancellationToken) =>
        Downstream.CallAsync(
            "Catalog",
            () => catalog.ListProductsAsync(q, category, currency ?? DefaultCurrency, after, limit, cancellationToken),
            page => TypedResults.Ok(page.ToShop()),
            cancellationToken);

    private static Task<IResult> Quote(IOrdersApiClient orders, CreateOrderRequest cart, CancellationToken cancellationToken)
    {
        cart.Currency ??= DefaultCurrency;

        return Downstream.CallAsync("Orders", () => orders.QuoteOrderAsync(cart, cancellationToken), quote => TypedResults.Ok(quote), cancellationToken);
    }

    /// <summary>
    /// Forwarded as it came, because Orders' answer is the contract: 201 with the order, the stored answer on a replay of
    /// the same key, 409 or 422 otherwise.
    /// </summary>
    private static Task<IResult> PlaceOrder(HttpContext context, IHttpClientFactory clients, CancellationToken cancellationToken) =>
        Downstream.ForwardAsync(
            context, clients.CreateClient(BffClients.OrdersClientName), "Orders", _ordersPath, _ordersPath, _shopOrdersPath, cancellationToken);

    private static Task<IResult> ListOrders(IOrdersApiClient orders, ClaimsPrincipal user, string? after, int? limit, CancellationToken cancellationToken) =>
        Downstream.CallAsync(
            "Orders",
            () => orders.ListOrdersAsync(ClientOf(user), null, null, null, null, after, limit, cancellationToken),
            page => TypedResults.Ok(page),
            cancellationToken);

    private static Task<IResult> GetOrder(IOrdersApiClient orders, Guid id, CancellationToken cancellationToken) =>
        Downstream.CallAsync("Orders", () => orders.GetOrderAsync(id, cancellationToken), order => TypedResults.Ok(order), cancellationToken);

    private static Guid? ClientOf(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(BffAuthentication.SubjectClaim), out var client) ? client : null;
}
