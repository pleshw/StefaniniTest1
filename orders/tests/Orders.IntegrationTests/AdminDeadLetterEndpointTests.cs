using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Orders.Application.DeadLetters;
using Orders.Domain;

namespace Orders.IntegrationTests;

/// <summary>The admin dead-letter API through the real Orders.Api pipeline over in-memory stores and a fake queue (no Docker).</summary>
public sealed class AdminDeadLetterEndpointTests : IAsyncDisposable
{
    private const string _path = "/api/v1/admin/dead-letters";

    private readonly OrdersApiFactory _factory = new();
    private readonly HttpClient _client;

    public AdminDeadLetterEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_ParkedMessages_ListsThemWithTheErrorHeadersAndLeavesThemInTheQueue()
    {
        var order = await PlaceOrder();
        _factory.DeadLetters.Park(Letter("m-1", order));
        _factory.DeadLetters.Park(Letter("m-2", order));

        var response = await Send(HttpMethod.Get, _path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var letters = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        letters.GetArrayLength().ShouldBe(2);
        letters[0].GetProperty("messageId").GetString().ShouldBe("m-1");
        letters[0].GetProperty("exceptionType").GetString().ShouldBe("System.InvalidOperationException");
        letters[0].GetProperty("exceptionMessage").GetString().ShouldBe("boom");
        letters[0].GetProperty("attempts").GetInt32().ShouldBe(4);
        letters[0].GetProperty("payload").GetString()!.ShouldContain(order.ToString());
        (await _factory.DeadLetters.PeekAsync(10, Token)).Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_Limit_CapsTheListAndAnInvalidLimitIs400()
    {
        var order = await PlaceOrder();
        foreach (var id in new[] { "m-1", "m-2", "m-3" })
        {
            _factory.DeadLetters.Park(Letter(id, order));
        }

        var limited = await (await Send(HttpMethod.Get, $"{_path}?limit=2")).Content.ReadFromJsonAsync<JsonElement>(Token);

        limited.GetArrayLength().ShouldBe(2);
        (await Send(HttpMethod.Get, $"{_path}?limit=0")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Get, $"{_path}?limit=101")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_EmptyQueue_ReturnsAnEmptyList()
    {
        var response = await Send(HttpMethod.Get, _path);

        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostReplay_ParkedMessage_RepublishesItRemovesItAndLogsTheReplayForTheOrder()
    {
        var order = await PlaceOrder();
        _factory.DeadLetters.Park(Letter("m-1", order));

        var response = await Send(HttpMethod.Post, $"{_path}/m-1/replay");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        body.GetProperty("replayedBy").GetString().ShouldBe("admin-1");
        body.GetProperty("exchange").GetString().ShouldBe("orders.events");
        body.GetProperty("newMessageId").GetString().ShouldNotBe("m-1");
        (await _factory.DeadLetters.PeekAsync(10, Token)).ShouldBeEmpty();
        var logged = _factory.EventLog.OtherEntries.ShouldHaveSingleItem();
        (logged.OrderId, logged.Type).ShouldBe((order, "DeadLetterReplayed.v1"));
        using var payload = JsonDocument.Parse(logged.Payload);
        payload.RootElement.GetProperty("messageId").GetString().ShouldBe("m-1");
        payload.RootElement.GetProperty("replayedBy").GetString().ShouldBe("admin-1");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostReplay_UnknownMessage_Returns404ProblemDetails()
    {
        var response = await Send(HttpMethod.Post, $"{_path}/nope/replay");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostReplay_MessageForAnOrderThatDoesNotExist_Returns422AndKeepsTheMessage()
    {
        _factory.DeadLetters.Park(Letter("m-1", Guid.NewGuid()));

        var response = await Send(HttpMethod.Post, $"{_path}/m-1/replay");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _factory.DeadLetters.PeekAsync(10, Token)).Count.ShouldBe(1);
        _factory.EventLog.OtherEntries.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Routes_BrokerUnreachable_Return503()
    {
        _factory.DeadLetters.Unreachable = true;

        (await Send(HttpMethod.Get, _path)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await Send(HttpMethod.Post, $"{_path}/m-1/replay")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Routes_SignedInWithoutTheAdminRole_Return403_AndAnonymousReturn401()
    {
        var order = await PlaceOrder();
        _factory.DeadLetters.Park(Letter("m-1", order));

        foreach (var (method, path) in new[] { (HttpMethod.Get, _path), (HttpMethod.Post, $"{_path}/m-1/replay") })
        {
            (await SendAs("11111111-1111-1111-1111-111111111111", null, method, path)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}");
            (await SendAs(null, null, method, path)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"{method} {path}");
        }

        (await _factory.DeadLetters.PeekAsync(10, Token)).Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OpenApi_ListsBothRoutes()
    {
        var paths = (await _client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Token)).GetProperty("paths");

        paths.GetProperty(_path).TryGetProperty("get", out _).ShouldBeTrue();
        paths.GetProperty($"{_path}/{{messageId}}/replay").TryGetProperty("post", out _).ShouldBeTrue();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static DeadLetterDto Letter(string messageId, Guid orderId) => new(
        messageId,
        "Orders.Contracts.OrderPlacedV1",
        "rabbitmq://queue/orders.worker.placed",
        "System.InvalidOperationException",
        "boom",
        4,
        $$"""{ "orderId": "{{orderId}}", "number": "ORD-1" }""",
        new Dictionary<string, string> { ["x-attempts"] = "4" });

    private async Task<Guid> PlaceOrder()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent("""{"currency":"BRL","items":[{"sku":"KB-1","quantity":1}]}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, "11111111-1111-1111-1111-111111111111");
        request.Headers.Add(TestAuthHandler.NameHeader, "Ana Souza");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await _client.SendAsync(request, Token);

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path) => SendAs("admin-1", "Admin", method, path);

    private async Task<HttpResponseMessage> SendAs(string? user, string? roles, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);

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

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
