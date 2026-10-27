using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Orders.Bff.UnitTests.Support;

namespace Orders.Bff.UnitTests;

/// <summary>The whole <c>/bff/admin</c> group, composed and pass-through routes alike, needs the Admin role.</summary>
public sealed class AdminPolicyTests : IAsyncDisposable
{
    private readonly BffFactory _factory = new();

    public static TheoryData<string, string> AdminRoutes => new()
    {
        { "GET", "/bff/admin/orders" },
        { "POST", "/bff/admin/orders" },
        { "GET", "/bff/admin/orders/6f1c2d3e-0000-4000-8000-000000000001" },
        { "GET", "/bff/admin/events" },
        { "GET", "/bff/admin/events/stream" },
        { "GET", "/bff/admin/discount-rules" },
        { "POST", "/bff/admin/orders/6f1c2d3e-0000-4000-8000-000000000001/confirm" },
        { "GET", "/bff/admin/dead-letters" },
        { "PUT", "/bff/admin/products/KB-1" },
    };

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    [Trait("Category", "Unit")]
    public async Task AdminRoute_Anonymous_Returns401AndCallsNothing(string method, string path)
    {
        var response = await Send(_factory.CreateBrowser(), method, path);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _factory.Downstream.Requests.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    [Trait("Category", "Unit")]
    public async Task AdminRoute_Shopper_Returns403AndCallsNothing(string method, string path)
    {
        var browser = _factory.CreateBrowser();
        await BffFactory.SignInAsync(browser, "shopper", BffFactory.ShopperPassword);

        var response = await Send(browser, method, path);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _factory.Downstream.Requests.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EveryAdminEndpoint_CarriesTheAdminPolicy()
    {
        var admin = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/bff/admin", StringComparison.Ordinal) == true)
            .ToList();

        admin.Count.ShouldBeGreaterThanOrEqualTo(9);
        admin.ShouldAllBe(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(data => data.Policy == "Admin"));
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static Task<HttpResponseMessage> Send(HttpClient browser, string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-CSRF", "1");

        if (method is "POST" or "PUT")
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }

        return browser.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
