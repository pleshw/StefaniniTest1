using Microsoft.AspNetCore.Diagnostics;
using Orders.Bff.Security;
using ServiceDefaults;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

namespace Orders.Bff.Routing;

/// <summary>
/// The pass-through admin routes: YARP forwards them to Orders or Catalog with the <c>/bff/admin</c> prefix replaced by
/// <c>/api/v1/admin</c>, and returns the service's answer (ProblemDetails included) unchanged. Composed routes are
/// endpoints of their own, not routes here.
/// </summary>
internal static class BffRoutes
{
    public const string OrdersCluster = "orders";
    public const string CatalogCluster = "catalog";

    private const string _actionPattern = "^(confirm|hold|block|cancel)$";

    public static IReadOnlyList<RouteConfig> Routes { get; } =
    [
        CatchAll("orders-discount-rules", OrdersCluster, "discount-rules"),
        CatchAll("orders-confirmation-rules", OrdersCluster, "confirmation-rules"),
        CatchAll("orders-dead-letters", OrdersCluster, "dead-letters"),
        Route("orders-actions", OrdersCluster, "/bff/admin/orders/{id:guid}/{action:regex(" + _actionPattern + ")}", "/api/v1/admin/orders/{id}/{action}"),
        // Catalog has no admin route on the bare collection, so a SKU is required.
        Route("catalog-products", CatalogCluster, "/bff/admin/products/{sku}/{**rest}", "/api/v1/admin/products/{sku}/{**rest}"),
    ];

    public static IReadOnlyList<ClusterConfig> Clusters(BffOptions options) =>
    [
        Cluster(OrdersCluster, options.Orders.BaseAddress),
        Cluster(CatalogCluster, options.Catalog.BaseAddress),
    ];

    public static IReverseProxyBuilder AddBffProxy(this IServiceCollection services, BffOptions options) =>
        services.AddReverseProxy()
            .LoadFromMemory(Routes, Clusters(options))
            .AddTransforms(transforms =>
            {
                var audience = transforms.Cluster?.ClusterId == CatalogCluster ? ServiceTokens.CatalogAudience : ServiceTokens.OrdersAudience;

                // The session cookie belongs to the browser and the BFF; the services never see it. What they get instead
                // is the BFF's token for the signed-in user; a header sent by the browser is never passed on.
                transforms.AddRequestHeaderRemove("Cookie");
                transforms.AddRequestHeaderRemove("Authorization");
                transforms.AddRequestTransform(context =>
                {
                    var issuer = context.HttpContext.RequestServices.GetRequiredService<ServiceTokenIssuer>();

                    if (issuer.TokenFor(context.HttpContext.User, audience) is { } token)
                    {
                        context.ProxyRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    }

                    // The service's own error response goes back as it is: no status-code page replaces an empty body.
                    if (context.HttpContext.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
                    {
                        statusCodePages.Enabled = false;
                    }

                    return ValueTask.CompletedTask;
                });
            });

    /// <summary>
    /// Maps the route table. When YARP itself fails (service unreachable, timeout) there is no service answer to pass
    /// on, so the BFF writes a ProblemDetails with YARP's status (502, 504).
    /// </summary>
    public static IEndpointConventionBuilder MapBffProxy(this IEndpointRouteBuilder endpoints) => endpoints.MapReverseProxy(proxy => proxy.Use(async (context, next) =>
    {
        await next();

        if (context.GetForwarderErrorFeature() is { } error && !context.Response.HasStarted)
        {
            await Results.Problem(
                statusCode: context.Response.StatusCode,
                title: "Service unavailable",
                detail: $"The request could not be forwarded ({error.Error}).").ExecuteAsync(context);
        }
    }));

    /// <summary><c>/bff/admin/{segment}</c> and everything below it.</summary>
    private static RouteConfig CatchAll(string id, string cluster, string segment) =>
        Route(id, cluster, $"/bff/admin/{segment}/{{**rest}}", $"/api/v1/admin/{segment}/{{**rest}}");

    private static RouteConfig Route(string id, string cluster, string path, string downstreamPath) =>
        new RouteConfig
        {
            RouteId = id,
            ClusterId = cluster,
            Match = new RouteMatch { Path = path },
        }.WithTransformPathRouteValues(downstreamPath);

    private static ClusterConfig Cluster(string id, string address) => new()
    {
        ClusterId = id,
        Destinations = new Dictionary<string, DestinationConfig> { [id] = new() { Address = address } },
    };
}
