using System.Net;
using System.Text;
using Orders.Bff.UnitTests.Support;

namespace Orders.Bff.UnitTests;

/// <summary>The pass-through route table: each public BFF path reaches the right service at the right path.</summary>
public sealed class BffRouteTests : IAsyncLifetime
{
    private const string _id = "6f1c2d3e-0000-4000-8000-000000000001";

    private readonly BffFactory _factory = new();
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync() => _client = await _factory.CreateAdminAsync();

    public static TheoryData<string, string, string> PassThroughRoutes => new()
    {
        { "GET", "/bff/admin/discount-rules", BffFactory.OrdersAddress + "/api/v1/admin/discount-rules" },
        { "POST", "/bff/admin/discount-rules", BffFactory.OrdersAddress + "/api/v1/admin/discount-rules" },
        { "GET", $"/bff/admin/discount-rules/{_id}", BffFactory.OrdersAddress + $"/api/v1/admin/discount-rules/{_id}" },
        { "PUT", $"/bff/admin/discount-rules/{_id}", BffFactory.OrdersAddress + $"/api/v1/admin/discount-rules/{_id}" },
        { "POST", $"/bff/admin/discount-rules/{_id}/deactivate", BffFactory.OrdersAddress + $"/api/v1/admin/discount-rules/{_id}/deactivate" },
        { "POST", "/bff/admin/discount-rules/preview", BffFactory.OrdersAddress + "/api/v1/admin/discount-rules/preview" },
        { "GET", "/bff/admin/confirmation-rules", BffFactory.OrdersAddress + "/api/v1/admin/confirmation-rules" },
        { "PUT", $"/bff/admin/confirmation-rules/{_id}", BffFactory.OrdersAddress + $"/api/v1/admin/confirmation-rules/{_id}" },
        { "POST", $"/bff/admin/confirmation-rules/{_id}/deactivate", BffFactory.OrdersAddress + $"/api/v1/admin/confirmation-rules/{_id}/deactivate" },
        { "GET", "/bff/admin/dead-letters", BffFactory.OrdersAddress + "/api/v1/admin/dead-letters" },
        { "POST", $"/bff/admin/dead-letters/{_id}/replay", BffFactory.OrdersAddress + $"/api/v1/admin/dead-letters/{_id}/replay" },
        { "POST", $"/bff/admin/orders/{_id}/confirm", BffFactory.OrdersAddress + $"/api/v1/admin/orders/{_id}/confirm" },
        { "POST", $"/bff/admin/orders/{_id}/hold", BffFactory.OrdersAddress + $"/api/v1/admin/orders/{_id}/hold" },
        { "POST", $"/bff/admin/orders/{_id}/block", BffFactory.OrdersAddress + $"/api/v1/admin/orders/{_id}/block" },
        { "POST", $"/bff/admin/orders/{_id}/cancel", BffFactory.OrdersAddress + $"/api/v1/admin/orders/{_id}/cancel" },
        { "GET", "/bff/admin/products/KB-1", BffFactory.CatalogAddress + "/api/v1/products/KB-1" },
        { "PUT", "/bff/admin/products/KB-1", BffFactory.CatalogAddress + "/api/v1/admin/products/KB-1" },
        { "PATCH", "/bff/admin/products/KB-1/price", BffFactory.CatalogAddress + "/api/v1/admin/products/KB-1/price" },
        { "POST", "/bff/admin/products/KB-1/deactivate", BffFactory.CatalogAddress + "/api/v1/admin/products/KB-1/deactivate" },
    };

    [Theory]
    [MemberData(nameof(PassThroughRoutes))]
    [Trait("Category", "Unit")]
    public async Task PublicPath_PassThroughRoute_ReachesTheServiceAtItsApiPath(string method, string path, string downstream)
    {
        var response = await Send(method, path + "?limit=5");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var forwarded = _factory.Downstream.Requests.ShouldHaveSingleItem();
        forwarded.Method.Method.ShouldBe(method);
        forwarded.Uri.ToString().ShouldBe(downstream + "?limit=5");
    }

    [Theory]
    [InlineData("POST", $"/bff/admin/orders/{_id}/delete")]
    [InlineData("POST", "/bff/admin/orders/not-a-guid/confirm")]
    [InlineData("GET", "/bff/admin/products")]
    [InlineData("GET", "/bff/admin/unknown")]
    [InlineData("GET", "/api/v1/orders")]
    [Trait("Category", "Unit")]
    public async Task PathOutsideTheRouteTable_IsNotForwarded(string method, string path)
    {
        var response = await Send(method, path);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _factory.Downstream.Requests.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProductRead_KeepsTheCurrencyQueryAndReturnsCatalogsETag()
    {
        _factory.Downstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"sku":"KB-1","version":3}""", Encoding.UTF8, "application/json"),
            Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"3\"") },
        };

        var response = await Send("GET", "/bff/admin/products/KB-1?currency=USD");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.ShouldBe("\"3\"");
        (await response.Content.ReadAsStringAsync(Token)).ShouldBe("""{"sku":"KB-1","version":3}""");
        _factory.Downstream.Requests.ShouldHaveSingleItem().Uri.ToString()
            .ShouldBe(BffFactory.CatalogAddress + "/api/v1/products/KB-1?currency=USD");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProductWrites_StillGoToTheAdminPath_NotTheProductRead()
    {
        await Send("PUT", "/bff/admin/products/KB-1");

        _factory.Downstream.Requests.ShouldHaveSingleItem().Uri.AbsolutePath.ShouldBe("/api/v1/admin/products/KB-1");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ForwardedRequest_KeepsBodyAndPreconditionHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/bff/admin/discount-rules/{_id}")
        {
            Content = new StringContent("""{"name":"Ten off"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"3\"");
        request.Headers.Add("Idempotency-Key", "a7f1c2d3-0000-4000-8000-000000000002");

        await _client.SendAsync(request, Token);

        var forwarded = _factory.Downstream.Requests.ShouldHaveSingleItem();
        forwarded.Body.ShouldBe("""{"name":"Ten off"}""");
        forwarded.Headers["If-Match"].ShouldBe("\"3\"");
        forwarded.Headers["Idempotency-Key"].ShouldBe("a7f1c2d3-0000-4000-8000-000000000002");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private Task<HttpResponseMessage> Send(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);

        if (method is "POST" or "PUT" or "PATCH")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return _client.SendAsync(request, Token);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
