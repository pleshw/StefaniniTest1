using Orders.Bff.Clients.Catalog;
using Orders.Bff.Clients.Orders;

namespace Orders.Bff.Clients;

/// <summary>
/// Typed clients generated from the committed OpenAPI documents of Orders and Catalog, for the composed routes. Each
/// keeps the default resilience handler of the service defaults.
/// </summary>
internal static class BffClients
{
    public const string OrdersClientName = "orders";
    public const string CatalogClientName = "catalog";

    public static IServiceCollection AddBffClients(this IServiceCollection services, BffOptions options)
    {
        services.AddHttpClient<IOrdersApiClient, OrdersApiClient>(OrdersClientName, http => http.BaseAddress = new Uri(options.Orders.BaseAddress));
        services.AddHttpClient<ICatalogApiClient, CatalogApiClient>(CatalogClientName, http => http.BaseAddress = new Uri(options.Catalog.BaseAddress));

        return services;
    }
}
