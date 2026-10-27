using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Orders.IntegrationTests;

/// <summary>Every Orders route group carries the authorization it needs: a signed-in user, or the Admin policy.</summary>
public sealed class EndpointPolicyTests : IAsyncDisposable
{
    private readonly OrdersApiFactory _factory = new();

    [Fact]
    [Trait("Category", "Unit")]
    public void AdminRoutes_RequireTheAdminPolicy()
    {
        var admin = Endpoints().Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/v1/admin", StringComparison.Ordinal)).ToList();

        admin.Count.ShouldBe(19);
        admin.ShouldAllBe(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(data => data.Policy == "Admin"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OrderRoutes_RequireASignedInUser()
    {
        var orders = Endpoints().Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/v1/orders", StringComparison.Ordinal)).ToList();

        orders.Count.ShouldBe(5);
        orders.ShouldAllBe(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0
            && endpoint.Metadata.GetMetadata<IAllowAnonymous>() == null);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private List<RouteEndpoint> Endpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1", StringComparison.Ordinal) == true)
            .ToList();
}
