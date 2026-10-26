using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Orders.Domain;

namespace Orders.IntegrationTests;

/// <summary>Admins placing orders on behalf of a client, through the real Orders.Api pipeline over in-memory stores.</summary>
public sealed class AdminCreateOrderEndpointTests : IClassFixture<OrdersApiFactory>
{
    private const string _admin = "admin-1";
    private const string _shopper = "11111111-1111-1111-1111-111111111111";
    private const string _targetClient = "44444444-4444-4444-4444-444444444444";

    private readonly OrdersApiFactory _factory;
    private readonly HttpClient _client;

    public AdminCreateOrderEndpointTests(OrdersApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostAdminOrder_Admin_Returns201ForTheTargetClientWithAdminChannelAndCreator()
    {
        var response = await Send(AdminBody(_targetClient, "Bruno Lima", ("KB-1", 2)), Guid.NewGuid().ToString(), _admin, "Admin");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var order = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var id = order.GetProperty("id").GetGuid();
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/orders/{id}");
        order.GetProperty("channel").GetString().ShouldBe("admin");
        order.GetProperty("client").GetProperty("id").GetGuid().ShouldBe(Guid.Parse(_targetClient));
        order.GetProperty("client").GetProperty("name").GetString().ShouldBe("Bruno Lima");
        order.GetProperty("total").GetDecimal().ShouldBe(39.98m);
        var stored = await _factory.Orders.GetByIdAsync(id, Token);
        stored!.CreatedBy.ShouldBe(_admin);
        stored.Channel.ShouldBe(OrderChannel.Admin);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostAdminOrder_SameKeyAndBodyAgain_ReplaysTheFirstResponse()
    {
        var key = Guid.NewGuid().ToString();
        var body = AdminBody(_targetClient, "Bruno Lima", ("KB-1", 1));
        var first = await Send(body, key, _admin, "Admin");
        var ordersAfterFirst = _factory.Orders.All.Count;

        var second = await Send(body, key, "admin-2", "Admin");

        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await second.Content.ReadAsStringAsync(Token)).ShouldBe(await first.Content.ReadAsStringAsync(Token));
        _factory.Orders.All.Count.ShouldBe(ordersAfterFirst);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostAdminOrder_SameKeyForAnotherClient_IsIndependent()
    {
        var key = Guid.NewGuid().ToString();

        var first = await Send(AdminBody(_targetClient, "Bruno Lima", ("KB-1", 1)), key, _admin, "Admin");
        var second = await Send(AdminBody("55555555-5555-5555-5555-555555555555", "Carla Dias", ("KB-1", 1)), key, _admin, "Admin");

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostAdminOrder_KeyUsedByTheClientOnTheShop_IsTheSameScope()
    {
        var key = Guid.NewGuid().ToString();
        var shop = await Send(ShopBody(null, ("KB-1", 1)), key, _shopper, roles: null, path: "/api/v1/orders");

        var admin = await Send(AdminBody(_shopper, "Ana Souza", ("KB-1", 1)), key, _admin, "Admin");

        shop.StatusCode.ShouldBe(HttpStatusCode.Created);
        admin.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostAdminOrder_SignedInWithoutTheAdminRole_Returns403()
    {
        var response = await Send(AdminBody(_targetClient, "Bruno Lima", ("KB-1", 1)), Guid.NewGuid().ToString(), _shopper, roles: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostAdminOrder_Anonymous_Returns401()
    {
        var response = await Send(AdminBody(_targetClient, "Bruno Lima", ("KB-1", 1)), Guid.NewGuid().ToString(), user: null, roles: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostAdminOrder_MissingClient_Returns400()
    {
        var noId = await Send("""{"clientName":"Bruno Lima","items":[{"sku":"KB-1","quantity":1}]}""", Guid.NewGuid().ToString(), _admin, "Admin");
        var noName = await Send($$"""{"clientId":"{{_targetClient}}","items":[{"sku":"KB-1","quantity":1}]}""", Guid.NewGuid().ToString(), _admin, "Admin");

        noId.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        noName.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostAdminOrder_MissingIdempotencyKey_Returns400()
    {
        var response = await Send(AdminBody(_targetClient, "Bruno Lima", ("KB-1", 1)), key: null, _admin, "Admin");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostShopOrder_BodyNamesAnotherClient_PlacesTheOrderForTheSignedInShopper()
    {
        var response = await Send(ShopBody(_targetClient, ("KB-1", 1)), Guid.NewGuid().ToString(), _shopper, roles: null, path: "/api/v1/orders");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var order = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        order.GetProperty("client").GetProperty("id").GetGuid().ShouldBe(Guid.Parse(_shopper));
        order.GetProperty("channel").GetString().ShouldBe("shop");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostAdminOrder_DefaultHost_ListsTheEndpointInOpenApi()
    {
        var document = await _client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Token);

        document.GetProperty("paths").GetProperty("/api/v1/admin/orders").TryGetProperty("post", out _).ShouldBeTrue();
    }

    private async Task<HttpResponseMessage> Send(string body, string? key, string? user, string? roles, string path = "/api/v1/admin/orders")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (user is not null)
        {
            request.Headers.Add(TestAuthHandler.UserHeader, user);
            request.Headers.Add(TestAuthHandler.NameHeader, "Signed In");
        }

        if (roles is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        }

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await _client.SendAsync(request, Token);
    }

    private static string AdminBody(string clientId, string clientName, params (string Sku, int Quantity)[] lines) =>
        JsonSerializer.Serialize(new { clientId, clientName, currency = "BRL", items = lines.Select(line => new { sku = line.Sku, quantity = line.Quantity }) });

    private static string ShopBody(string? clientId, params (string Sku, int Quantity)[] lines) =>
        JsonSerializer.Serialize(new { clientId, currency = "BRL", items = lines.Select(line => new { sku = line.Sku, quantity = line.Quantity }) });

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
