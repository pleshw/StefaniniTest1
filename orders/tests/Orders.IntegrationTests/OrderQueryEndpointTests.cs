using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Orders.IntegrationTests;

/// <summary>The order query routes through the real Orders.Api pipeline over in-memory stores (no Docker).</summary>
public sealed class OrderQueryEndpointTests : IClassFixture<OrdersApiFactory>, IAsyncLifetime
{
    // Fresh clients per test: the API fixture, and so its in-memory store, is shared by the whole class.
    private readonly string _ana = Guid.NewGuid().ToString();
    private readonly string _bruno = Guid.NewGuid().ToString();

    private readonly HttpClient _client;
    private readonly List<Guid> _anaOrders = [];
    private Guid _brunoOrder;

    public OrderQueryEndpointTests(OrdersApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    public async ValueTask InitializeAsync()
    {
        _anaOrders.Add(await Create(_ana, "Ana Souza"));
        _anaOrders.Add(await Create(_ana, "Ana Souza"));
        _brunoOrder = await Create(_bruno, "Bruno Lima");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListOrders_Shopper_SeesOnlyTheirOwnOrders()
    {
        var page = await Get(_ana, roles: null, "/api/v1/orders");

        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var ids = Ids(await page.Content.ReadFromJsonAsync<JsonElement>(Token));
        ids.ShouldContain(_anaOrders[0]);
        ids.ShouldContain(_anaOrders[1]);
        ids.ShouldNotContain(_brunoOrder);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListOrders_ShopperAskingForAnotherClient_Returns403()
    {
        var response = await Get(_ana, roles: null, $"/api/v1/orders?clientId={_bruno}");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListOrders_AdminWithClientFilter_SeesThatClientsOrders()
    {
        var response = await Get("admin-1", "Admin", $"/api/v1/orders?clientId={_bruno}");

        Ids(await response.Content.ReadFromJsonAsync<JsonElement>(Token)).ShouldBe([_brunoOrder]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListOrders_StatusAndSearchFilters_AreApplied()
    {
        var pending = await Get(_ana, roles: null, "/api/v1/orders?status=pending&q=souza");
        var confirmed = await Get(_ana, roles: null, "/api/v1/orders?status=Confirmed");

        Ids(await pending.Content.ReadFromJsonAsync<JsonElement>(Token)).Count.ShouldBe(2);
        Ids(await confirmed.Content.ReadFromJsonAsync<JsonElement>(Token)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListOrders_LimitOne_PagesWithTheNextCursor()
    {
        var first = await (await Get(_ana, roles: null, "/api/v1/orders?limit=1")).Content.ReadFromJsonAsync<JsonElement>(Token);
        var next = first.GetProperty("next").GetString();
        var second = await (await Get(_ana, roles: null, $"/api/v1/orders?limit=1&after={next}")).Content.ReadFromJsonAsync<JsonElement>(Token);

        Ids(first).Concat(Ids(second)).ShouldBe(_anaOrders, ignoreOrder: true);
        second.GetProperty("next").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("status=Shipped")]
    [InlineData("after=garbage")]
    public async Task ListOrders_InvalidParameters_Return400(string query)
    {
        var response = await Get(_ana, roles: null, $"/api/v1/orders?{query}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetOrder_OwnOrder_ReturnsItWithItems()
    {
        var response = await Get(_ana, roles: null, $"/api/v1/orders/{_anaOrders[0]}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var order = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        order.GetProperty("id").GetGuid().ShouldBe(_anaOrders[0]);
        order.GetProperty("items").GetArrayLength().ShouldBe(1);
        order.GetProperty("itemCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetOrder_AnotherClientsOrder_Returns404()
    {
        var response = await Get(_ana, roles: null, $"/api/v1/orders/{_brunoOrder}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetOrder_Admin_SeesAnyClientsOrder()
    {
        var response = await Get("admin-1", "Admin", $"/api/v1/orders/{_brunoOrder}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetOrder_UnknownId_Returns404()
    {
        var response = await Get(_ana, roles: null, $"/api/v1/orders/{Guid.NewGuid()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetOrderHistory_OwnOrder_ReturnsThePendingEntry()
    {
        var response = await Get(_ana, roles: null, $"/api/v1/orders/{_anaOrders[0]}/history");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entry = (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).EnumerateArray().ShouldHaveSingleItem();
        entry.GetProperty("to").GetString().ShouldBe("Pending");
        entry.GetProperty("seq").GetInt32().ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetOrderHistory_AnotherClientsOrder_Returns404()
    {
        var response = await Get(_ana, roles: null, $"/api/v1/orders/{_brunoOrder}/history");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("/api/v1/orders")]
    [InlineData("/api/v1/orders/22222222-2222-2222-2222-222222222222")]
    [InlineData("/api/v1/orders/22222222-2222-2222-2222-222222222222/history")]
    public async Task QueryRoutes_Anonymous_Return401(string path)
    {
        var response = await Get(user: null, roles: null, path);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task QueryRoutes_ShopperWithoutAClientId_Return403()
    {
        var response = await Get("not-a-guid", roles: null, "/api/v1/orders");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task QueryRoutes_DefaultHost_AreListedInOpenApi()
    {
        var paths = (await _client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Token)).GetProperty("paths");

        paths.GetProperty("/api/v1/orders").TryGetProperty("get", out _).ShouldBeTrue();
        paths.GetProperty("/api/v1/orders/{id}").TryGetProperty("get", out _).ShouldBeTrue();
        paths.GetProperty("/api/v1/orders/{id}/history").TryGetProperty("get", out _).ShouldBeTrue();
    }

    private async Task<Guid> Create(string user, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent("""{"items":[{"sku":"KB-1","quantity":2}]}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.Add(TestAuthHandler.NameHeader, name);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request, Token);

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> Get(string? user, string? roles, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (user is not null)
        {
            request.Headers.Add(TestAuthHandler.UserHeader, user);
        }

        if (roles is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        }

        return await _client.SendAsync(request, Token);
    }

    private static List<Guid> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(order => order.GetProperty("id").GetGuid()).ToList();

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
