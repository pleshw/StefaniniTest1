using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog.IntegrationTests;

/// <summary>Admin product routes carry the Admin policy; product queries are open to internal callers.</summary>
public sealed class EndpointPolicyTests : IAsyncDisposable
{
    private readonly CatalogApiFactory _factory = new();

    [Fact]
    [Trait("Category", "Unit")]
    public void AdminRoutes_RequireTheAdminPolicy()
    {
        var admin = Endpoints().Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/v1/admin", StringComparison.Ordinal)).ToList();

        admin.Count.ShouldBe(3);
        admin.ShouldAllBe(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(data => data.Policy == "Admin"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProductRoutes_NeedNoUser()
    {
        var products = Endpoints().Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/v1/products", StringComparison.Ordinal)).ToList();

        products.Count.ShouldBe(3);
        products.ShouldAllBe(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private List<RouteEndpoint> Endpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1", StringComparison.Ordinal) == true)
            .ToList();
}
