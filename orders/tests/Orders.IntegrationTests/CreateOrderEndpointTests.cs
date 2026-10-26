using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Orders.Domain;

namespace Orders.IntegrationTests;

/// <summary>The real Orders.Api pipeline and handlers over in-memory stores (no Docker).</summary>
public sealed class CreateOrderEndpointTests : IClassFixture<OrdersApiFactory>
{
    private const string _shopper = "11111111-1111-1111-1111-111111111111";

    private readonly OrdersApiFactory _factory;
    private readonly HttpClient _client;

    public CreateOrderEndpointTests(OrdersApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_ValidCart_Returns201WithLocationAndThePricedOrder()
    {
        var response = await Post(Body(("KB-1", 2), ("MS-1", 1)), Guid.NewGuid().ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var order = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/orders/{order.GetProperty("id").GetGuid()}");
        order.GetProperty("number").GetString()!.ShouldStartWith("ORD-");
        order.GetProperty("status").GetString().ShouldBe("Pending");
        order.GetProperty("channel").GetString().ShouldBe("shop");
        order.GetProperty("currency").GetString().ShouldBe("BRL");
        order.GetProperty("itemCount").GetInt32().ShouldBe(3);
        order.GetProperty("subtotal").GetDecimal().ShouldBe(49.98m);
        order.GetProperty("discountTotal").GetDecimal().ShouldBe(0m);
        order.GetProperty("total").GetDecimal().ShouldBe(49.98m);
        order.GetProperty("client").GetProperty("id").GetGuid().ShouldBe(Guid.Parse(_shopper));
        order.GetProperty("client").GetProperty("name").GetString().ShouldBe("Ana Souza");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_ValidCart_StoresTheOrderAndPublishesOrderPlaced()
    {
        var response = await Post(Body(("KB-1", 1)), Guid.NewGuid().ToString());
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();

        var stored = await _factory.Orders.GetByIdAsync(id, Token);

        stored.ShouldNotBeNull();
        stored.Channel.ShouldBe(OrderChannel.Shop);
        stored.CreatedBy.ShouldBe(_shopper);
        stored.StatusHistory.ShouldHaveSingleItem().To.ShouldBe(OrderStatus.Pending);
        _factory.Events.Published.OfType<OrderPlaced>().ShouldContain(placed => placed.OrderId == id);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_TenIdenticalRequestsInParallel_CreateOneOrderAndReturnTenIdenticalResponses()
    {
        var key = Guid.NewGuid().ToString();
        var ordersBefore = _factory.Orders.All.Count;

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => PostAndRead(Body(("KB-1", 1), ("MS-1", 2)), key)));

        responses.ShouldAllBe(response => response.Status == HttpStatusCode.Created);
        responses.Select(response => response.Body).Distinct().Count().ShouldBe(1);
        responses.Select(response => response.Location).Distinct().Count().ShouldBe(1);
        _factory.Orders.All.Count.ShouldBe(ordersBefore + 1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_SameKeyAndBodyAgain_ReplaysWithoutPricingAgain()
    {
        var key = Guid.NewGuid().ToString();
        var first = await PostAndRead(Body(("KB-1", 1)), key);
        var callsAfterFirst = _factory.Catalog.Calls;

        var second = await PostAndRead(Body(("KB-1", 1)), key);

        second.ShouldBe(first);
        _factory.Catalog.Calls.ShouldBe(callsAfterFirst);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_SameKeyWithAnotherBody_Returns422()
    {
        var key = Guid.NewGuid().ToString();
        await Post(Body(("KB-1", 1)), key);

        var response = await Post(Body(("KB-1", 2)), key);

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_SameKeyWithPropertiesInAnotherOrder_IsTheSameRequest()
    {
        var key = Guid.NewGuid().ToString();
        var first = await PostAndRead("""{"currency":"BRL","items":[{"sku":"KB-1","quantity":1}]}""", key);

        var second = await PostAndRead("""{"items":[{"quantity":1,"sku":"KB-1"}],"currency":"BRL"}""", key);

        second.ShouldBe(first);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    public async Task PostOrder_MissingOrInvalidIdempotencyKey_Returns400(string? key)
    {
        var response = await Post(Body(("KB-1", 1)), key);

        await AssertProblem(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_UnknownOrInactiveSku_Returns422ListingThem()
    {
        var response = await Post(Body(("NOPE-1", 1), ("OFF-1", 1)), Guid.NewGuid().ToString());

        var problem = await AssertProblem(response, HttpStatusCode.UnprocessableEntity);
        var detail = problem.GetProperty("detail").GetString();
        detail!.ShouldContain("NOPE-1");
        detail!.ShouldContain("OFF-1");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_EmptyCartOrBadQuantity_Returns400()
    {
        await AssertProblem(await Post("""{"items":[]}""", Guid.NewGuid().ToString()), HttpStatusCode.BadRequest);
        await AssertProblem(await Post(Body(("KB-1", 0)), Guid.NewGuid().ToString()), HttpStatusCode.BadRequest);
        await AssertProblem(await Post("""{"currency":"REAL","items":[{"sku":"KB-1","quantity":1}]}""", Guid.NewGuid().ToString()), HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_CatalogDown_Returns503AndCreatesNothing()
    {
        await using var factory = new OrdersApiFactory();
        factory.Catalog.Unavailable = true;
        using var client = factory.CreateClient();

        using var request = Request(Body(("KB-1", 1)), Guid.NewGuid().ToString(), _shopper);
        var response = await client.SendAsync(request, Token);

        await AssertProblem(response, HttpStatusCode.ServiceUnavailable);
        factory.Orders.All.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_Anonymous_Returns401()
    {
        using var request = Request(Body(("KB-1", 1)), Guid.NewGuid().ToString(), user: null);

        var response = await _client.SendAsync(request, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_UserWithoutAClientId_Returns403()
    {
        using var request = Request(Body(("KB-1", 1)), Guid.NewGuid().ToString(), user: "not-a-guid");

        var response = await _client.SendAsync(request, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_SameKeyFromAnotherShopper_IsIndependent()
    {
        var key = Guid.NewGuid().ToString();
        var first = await Post(Body(("KB-1", 1)), key);
        using var request = Request(Body(("KB-1", 1)), key, user: "33333333-3333-3333-3333-333333333333");

        var second = await _client.SendAsync(request, Token);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_DefaultHost_ListsTheEndpointInOpenApi()
    {
        var document = await _client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Token);

        document.GetProperty("paths").GetProperty("/api/v1/orders").TryGetProperty("post", out _).ShouldBeTrue();
    }

    private async Task<HttpResponseMessage> Post(string body, string? key)
    {
        using var request = Request(body, key, _shopper);

        return await _client.SendAsync(request, Token);
    }

    private async Task<(HttpStatusCode Status, string Body, string? Location)> PostAndRead(string body, string? key)
    {
        var response = await Post(body, key);

        return (response.StatusCode, await response.Content.ReadAsStringAsync(Token), response.Headers.Location?.ToString());
    }

    private static HttpRequestMessage Request(string body, string? key, string? user)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

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

    private static string Body(params (string Sku, int Quantity)[] lines) =>
        JsonSerializer.Serialize(new { currency = "BRL", items = lines.Select(line => new { sku = line.Sku, quantity = line.Quantity }) });

    private static async Task<JsonElement> AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        problem.GetProperty("status").GetInt32().ShouldBe((int)status);

        return problem;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
