using Orders.Bff.Clients.Catalog;
using Orders.Bff.Clients.Orders;
using Orders.Bff.Security;
using ServiceDefaults;

namespace Orders.Bff.Clients;

/// <summary>
/// Typed clients generated from the committed OpenAPI documents of Orders and Catalog, for the composed routes. Each
/// keeps the default resilience handler of the service defaults and sends the signed-in user's service token.
/// </summary>
internal static class BffClients
{
    public const string OrdersClientName = "orders";
    public const string CatalogClientName = "catalog";

    public static IServiceCollection AddBffClients(this IServiceCollection services, BffOptions options)
    {
        services.AddHttpClient<IOrdersApiClient, OrdersApiClient>(OrdersClientName, http => http.BaseAddress = new Uri(options.Orders.BaseAddress))
            .AddHttpMessageHandler(provider => TokenHandler(provider, ServiceTokens.OrdersAudience));
        services.AddHttpClient<ICatalogApiClient, CatalogApiClient>(CatalogClientName, http => http.BaseAddress = new Uri(options.Catalog.BaseAddress))
            .AddHttpMessageHandler(provider => TokenHandler(provider, ServiceTokens.CatalogAudience));

        return services;
    }

    private static DownstreamTokenHandler TokenHandler(IServiceProvider provider, string audience) =>
        new(provider.GetRequiredService<IHttpContextAccessor>(), provider.GetRequiredService<ServiceTokenIssuer>(), audience);
}
