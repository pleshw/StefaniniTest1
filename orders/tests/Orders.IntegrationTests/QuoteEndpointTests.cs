using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Orders.IntegrationTests;

/// <summary>POST /api/v1/orders/quote through the real Orders.Api pipeline over in-memory stores (no Docker).</summary>
public sealed class QuoteEndpointTests : IClassFixture<OrdersApiFactory>
{
    private const string _shopper = "11111111-1111-1111-1111-111111111111";
    private const string _cart = """{"currency":"BRL","items":[{"sku":"KB-1","quantity":2},{"sku":"MS-1","quantity":1},{"sku":"kb-1","quantity":1}]}""";

    private readonly OrdersApiFactory _factory;
    private readonly HttpClient _client;

    public QuoteEndpointTests(OrdersApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostQuote_SameCartAsACreatedOrder_HasTheSameTotalsAndLines()
    {
        var quote = await Read(await Send("/api/v1/orders/quote", _cart, key: null));
        var order = await Read(await Send("/api/v1/orders", _cart, Guid.NewGuid().ToString()));

        foreach (var property in new[] { "currency", "itemCount", "subtotal", "discountTotal", "total" })
        {
            quote.GetProperty(property).GetRawText().ShouldBe(order.GetProperty(property).GetRawText(), property);
        }

        quote.GetProperty("items").GetRawText().ShouldBe(order.GetProperty("items").GetRawText());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostQuote_ValidCart_PersistsNothingAndNeedsNoIdempotencyKey()
    {
        var ordersBefore = _factory.Orders.All.Count;
        var eventsBefore = _factory.Events.Published.Count;

        var response = await Send("/api/v1/orders/quote", _cart, key: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Read(response)).GetProperty("total").GetDecimal().ShouldBe(69.97m);
        _factory.Orders.All.Count.ShouldBe(ordersBefore);
        _factory.Events.Published.Count.ShouldBe(eventsBefore);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("""{"items":[]}""")]
    [InlineData("""{"items":[{"sku":"KB-1","quantity":0}]}""")]
    [InlineData("""{"items":[{"quantity":1}]}""")]
    [InlineData("""{"currency":"REAL","items":[{"sku":"KB-1","quantity":1}]}""")]
    public async Task PostQuote_InvalidCart_Returns400LikeCreate(string body)
    {
        var quote = await Send("/api/v1/orders/quote", body, key: null);
        var create = await Send("/api/v1/orders", body, Guid.NewGuid().ToString());

        quote.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        create.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        quote.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostQuote_UnknownOrInactiveSku_Returns422LikeCreate()
    {
        const string body = """{"items":[{"sku":"NOPE-1","quantity":1},{"sku":"OFF-1","quantity":1}]}""";

        var quote = await Send("/api/v1/orders/quote", body, key: null);
        var create = await Send("/api/v1/orders", body, Guid.NewGuid().ToString());

        quote.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        create.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Read(quote)).GetProperty("detail").GetString().ShouldBe((await Read(create)).GetProperty("detail").GetString());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostQuote_CatalogDown_Returns503()
    {
        await using var factory = new OrdersApiFactory();
        factory.Catalog.Unavailable = true;
        using var client = factory.CreateClient();
        using var request = Request("/api/v1/orders/quote", _cart, key: null, _shopper);

        var response = await client.SendAsync(request, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostQuote_Anonymous_Returns401()
    {
        using var request = Request("/api/v1/orders/quote", _cart, key: null, user: null);

        (await _client.SendAsync(request, Token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostQuote_UserWithoutAClientId_Returns403()
    {
        using var request = Request("/api/v1/orders/quote", _cart, key: null, user: "not-a-guid");

        (await _client.SendAsync(request, Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostQuote_DefaultHost_ListsTheEndpointInOpenApi()
    {
        var document = await _client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Token);

        document.GetProperty("paths").GetProperty("/api/v1/orders/quote").TryGetProperty("post", out _).ShouldBeTrue();
    }

    private async Task<HttpResponseMessage> Send(string path, string body, string? key)
    {
        using var request = Request(path, body, key, _shopper);

        return await _client.SendAsync(request, Token);
    }

    private static HttpRequestMessage Request(string path, string body, string? key, string? user)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        if (user is not null)
        {
            request.Headers.Add(TestAuthHandler.UserHeader, user);
            request.Headers.Add(TestAuthHandler.NameHeader, "Ana Souza");
        }

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return request;
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Token);

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
