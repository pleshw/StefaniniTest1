using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Orders.IntegrationTests;

/// <summary>The admin confirmation-rules API through the real Orders.Api pipeline over in-memory stores (no Docker).</summary>
public sealed class AdminConfirmationRuleEndpointTests : IAsyncDisposable
{
    private const string _path = "/api/v1/admin/confirmation-rules";
    private const string _large = """{ "name": "Large BRL order", "conditions": [ { "kind": "TotalAbove", "amount": 5000, "currency": "BRL" } ], "priority": 1 }""";

    private readonly OrdersApiFactory _factory = new();
    private readonly HttpClient _client;

    public AdminConfirmationRuleEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostRule_Admin_Returns201WithLocationETagAndTheStoredRule()
    {
        var response = await Send(HttpMethod.Post, _path, _large);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var rule = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var id = rule.GetProperty("id").GetGuid();
        response.Headers.Location!.ToString().ShouldBe($"{_path}/{id}");
        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
        rule.GetProperty("version").GetInt32().ShouldBe(1);
        rule.GetProperty("action").GetString().ShouldBe("Hold");
        rule.GetProperty("conditions")[0].GetProperty("kind").GetString().ShouldBe("TotalAbove");
        rule.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        rule.GetProperty("createdBy").GetString().ShouldBe("admin-1");
        _factory.ConfirmationRules.Invalidations.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetRule_AfterCreate_ReturnsItWithItsETag_AndUnknownIs404()
    {
        var id = await CreateRule();

        var found = await Send(HttpMethod.Get, $"{_path}/{id}");
        var missing = await Send(HttpMethod.Get, $"{_path}/{Guid.NewGuid()}");

        found.StatusCode.ShouldBe(HttpStatusCode.OK);
        found.Headers.ETag!.Tag.ShouldBe("\"1\"");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetRules_ListsTheLatestVersionOfEveryRuleAndFiltersByActive()
    {
        var first = await CreateRule("First", priority: 1);
        var second = await CreateRule("Second", priority: 2);
        await Send(HttpMethod.Post, $"{_path}/{second}/deactivate");
        await Send(HttpMethod.Put, $"{_path}/{first}", Body("First v2", 1), ifMatch: "\"1\"");

        var all = await (await Send(HttpMethod.Get, _path)).Content.ReadFromJsonAsync<JsonElement>(Token);
        var active = await (await Send(HttpMethod.Get, $"{_path}?active=true")).Content.ReadFromJsonAsync<JsonElement>(Token);
        var inactive = await (await Send(HttpMethod.Get, $"{_path}?active=false")).Content.ReadFromJsonAsync<JsonElement>(Token);

        all.GetArrayLength().ShouldBe(2);
        all[0].GetProperty("name").GetString().ShouldBe("First v2");
        all[0].GetProperty("version").GetInt32().ShouldBe(2);
        active.EnumerateArray().Select(rule => rule.GetProperty("id").GetGuid()).ShouldBe([first]);
        inactive.EnumerateArray().Select(rule => rule.GetProperty("id").GetGuid()).ShouldBe([second]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutRule_CurrentVersion_CreatesTheNextVersionKeepsTheOldOneAndInvalidatesTheCache()
    {
        var id = await CreateRule();

        var response = await Send(HttpMethod.Put, $"{_path}/{id}", Body("Stricter", 1, amount: 9000), ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.ShouldBe("\"2\"");
        var rule = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        rule.GetProperty("version").GetInt32().ShouldBe(2);
        rule.GetProperty("conditions")[0].GetProperty("amount").GetDecimal().ShouldBe(9000m);
        _factory.ConfirmationRuleStore.Versions.Where(stored => stored.Id == id).Select(stored => stored.Version).Order().ShouldBe([1, 2]);
        _factory.ConfirmationRules.Invalidations.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutRule_StaleVersion_Returns412AndWithoutIfMatch428AndNothingIsStored()
    {
        var id = await CreateRule();
        await Send(HttpMethod.Put, $"{_path}/{id}", Body("Stricter", 1, amount: 9000), ifMatch: "\"1\"");
        var invalidations = _factory.ConfirmationRules.Invalidations;

        var stale = await Send(HttpMethod.Put, $"{_path}/{id}", Body("Again", 1), ifMatch: "\"1\"");
        var missing = await Send(HttpMethod.Put, $"{_path}/{id}", Body("Again", 1));

        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        stale.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        missing.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        _factory.ConfirmationRuleStore.Versions.Count(stored => stored.Id == id).ShouldBe(2);
        _factory.ConfirmationRules.Invalidations.ShouldBe(invalidations);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutRule_UnknownRule_Returns404()
    {
        (await Send(HttpMethod.Put, $"{_path}/{Guid.NewGuid()}", Body("x", 1), ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostDeactivate_StoresAnInactiveNextVersion_AndAStaleVersionIs412()
    {
        var id = await CreateRule();

        var stale = await Send(HttpMethod.Post, $"{_path}/{id}/deactivate", ifMatch: "\"7\"");
        var response = await Send(HttpMethod.Post, $"{_path}/{id}/deactivate", ifMatch: "\"1\"");

        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.ShouldBe("\"2\"");
        var rule = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        rule.GetProperty("isActive").GetBoolean().ShouldBeFalse();
        rule.GetProperty("name").GetString().ShouldBe("Large BRL order");
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("""{ "name": "x", "conditions": [] }""", "conditions")]
    [InlineData("""{ "name": "x" }""", "conditions")]
    [InlineData("""{ "name": "x", "conditions": [ { "kind": "Nope" } ] }""", "conditions")]
    [InlineData("""{ "name": "x", "conditions": [ { "kind": "MinSubtotal", "amount": 5, "currency": "BRL" } ] }""", "TotalAbove")]
    [InlineData("""{ "name": "x", "conditions": [ { "kind": "TotalAbove", "amount": -5, "currency": "BRL" } ] }""", "conditions")]
    [InlineData("""{ "name": "x", "conditions": { "kind": "FirstOrderOfClient" } }""", "conditions")]
    [InlineData("""{ "conditions": [ { "kind": "FirstOrderOfClient" } ] }""", "name")]
    [InlineData("""{ "name": "x", "action": "Block", "conditions": [ { "kind": "FirstOrderOfClient" } ] }""", "action")]
    public async Task PostRule_InvalidRule_Returns400ProblemDetailsAndStoresNothing(string body, string mentions)
    {
        var response = await Send(HttpMethod.Post, _path, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadAsStringAsync(Token)).ShouldContain(mentions);
        _factory.ConfirmationRuleStore.Versions.ShouldBeEmpty();
        _factory.ConfirmationRules.Invalidations.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostRule_FirstOrderAndChannelConditions_AreAccepted()
    {
        var response = await Send(
            HttpMethod.Post, _path, """{ "name": "First shop order", "conditions": [ { "kind": "FirstOrderOfClient" }, { "kind": "ChannelIs", "channel": "shop" } ], "priority": 3 }""");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AdminRoutes_SignedInWithoutTheAdminRole_Return403_AndAnonymousReturn401()
    {
        var id = await CreateRule();

        foreach (var (method, path, body) in new (HttpMethod, string, string?)[]
        {
            (HttpMethod.Get, _path, null),
            (HttpMethod.Post, _path, _large),
            (HttpMethod.Get, $"{_path}/{id}", null),
            (HttpMethod.Put, $"{_path}/{id}", _large),
            (HttpMethod.Post, $"{_path}/{id}/deactivate", null),
        })
        {
            (await SendAs("11111111-1111-1111-1111-111111111111", method, path, body, "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}");
            (await SendAs(null, method, path, body, "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"{method} {path}");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OpenApi_ListsTheAdminConfirmationRuleRoutes()
    {
        var document = await _client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Token);
        var paths = document.GetProperty("paths");

        paths.GetProperty(_path).TryGetProperty("get", out _).ShouldBeTrue();
        paths.GetProperty(_path).TryGetProperty("post", out _).ShouldBeTrue();
        paths.GetProperty($"{_path}/{{id}}").TryGetProperty("put", out _).ShouldBeTrue();
        paths.GetProperty($"{_path}/{{id}}/deactivate").TryGetProperty("post", out _).ShouldBeTrue();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static string Body(string name, int priority, int amount = 5000) =>
        $$"""{ "name": "{{name}}", "conditions": [ { "kind": "TotalAbove", "amount": {{amount}}, "currency": "BRL" } ], "priority": {{priority}} }""";

    private async Task<Guid> CreateRule(string name = "Large BRL order", int priority = 1)
    {
        var response = await Send(HttpMethod.Post, _path, Body(name, priority));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body = null, string? ifMatch = null) =>
        SendAs("admin-1", method, path, body, ifMatch, "Admin");

    private async Task<HttpResponseMessage> SendAs(string? user, HttpMethod method, string path, string? body = null, string? ifMatch = null, string? roles = null)
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

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await _client.SendAsync(request, Token);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
