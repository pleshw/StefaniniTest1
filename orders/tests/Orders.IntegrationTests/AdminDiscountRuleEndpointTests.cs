using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Orders.IntegrationTests;

/// <summary>The admin discount-rules API through the real Orders.Api pipeline over in-memory stores (no Docker).</summary>
public sealed class AdminDiscountRuleEndpointTests : IAsyncDisposable
{
    private const string _rulesPath = "/api/v1/admin/discount-rules";
    private const string _cart = """{"currency":"BRL","items":[{"sku":"KB-1","quantity":2},{"sku":"MS-1","quantity":1}]}""";
    private const string _tenPercent = """{ "name": "Ten percent", "type": "Percentage", "parameters": { "percent": 10, "appliesTo": "order" }, "priority": 1, "stackable": false, "oncePerClient": false }""";
    private const string _shopper = "11111111-1111-1111-1111-111111111111";

    private readonly OrdersApiFactory _factory = new() { CachedDiscountRules = true };
    private readonly HttpClient _client;

    public AdminDiscountRuleEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostRule_Admin_Returns201WithLocationAndETagOfVersionOne()
    {
        var response = await Send(HttpMethod.Post, _rulesPath, _tenPercent);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var rule = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var id = rule.GetProperty("id").GetGuid();
        response.Headers.Location!.ToString().ShouldBe($"{_rulesPath}/{id}");
        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
        rule.GetProperty("version").GetInt32().ShouldBe(1);
        rule.GetProperty("type").GetString().ShouldBe("Percentage");
        rule.GetProperty("parameters").GetProperty("percent").GetDecimal().ShouldBe(10m);
        rule.GetProperty("conditions").GetArrayLength().ShouldBe(0);
        rule.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        rule.GetProperty("createdBy").GetString().ShouldBe("admin-1");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetRule_AfterCreate_ReturnsTheLatestVersionWithItsETag()
    {
        var id = await CreateRule();

        var response = await Send(HttpMethod.Get, $"{_rulesPath}/{id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("name").GetString().ShouldBe("Ten percent");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetRule_Unknown_Returns404ProblemDetails()
    {
        var response = await Send(HttpMethod.Get, $"{_rulesPath}/{Guid.NewGuid()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetRules_ListsTheLatestVersionOfEveryRuleAndFiltersByActive()
    {
        var first = await CreateRule();
        var second = await CreateRule("Second", priority: 2);
        await Send(HttpMethod.Post, $"{_rulesPath}/{second}/deactivate");
        await Send(HttpMethod.Put, $"{_rulesPath}/{first}", Body("First v2", 1), ifMatch: "\"1\"");

        var all = await (await Send(HttpMethod.Get, _rulesPath)).Content.ReadFromJsonAsync<JsonElement>(Token);
        var active = await (await Send(HttpMethod.Get, $"{_rulesPath}?active=true")).Content.ReadFromJsonAsync<JsonElement>(Token);
        var inactive = await (await Send(HttpMethod.Get, $"{_rulesPath}?active=false")).Content.ReadFromJsonAsync<JsonElement>(Token);

        all.GetArrayLength().ShouldBe(2);
        all[0].GetProperty("name").GetString().ShouldBe("First v2");
        all[0].GetProperty("version").GetInt32().ShouldBe(2);
        active.EnumerateArray().Select(rule => rule.GetProperty("id").GetGuid()).ShouldBe([first]);
        inactive.EnumerateArray().Select(rule => rule.GetProperty("id").GetGuid()).ShouldBe([second]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutRule_CurrentVersion_CreatesTheNextVersionAndKeepsTheOldOne()
    {
        var id = await CreateRule();

        var response = await Send(HttpMethod.Put, $"{_rulesPath}/{id}", Body("Twenty", 1, percent: 20), ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.ShouldBe("\"2\"");
        var rule = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        rule.GetProperty("version").GetInt32().ShouldBe(2);
        rule.GetProperty("parameters").GetProperty("percent").GetDecimal().ShouldBe(20m);
        _factory.DiscountRuleStore.Versions.Where(stored => stored.Id == id).Select(stored => stored.Version).Order().ShouldBe([1, 2]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutRule_StaleVersion_Returns412AndStoresNothing()
    {
        var id = await CreateRule();
        await Send(HttpMethod.Put, $"{_rulesPath}/{id}", Body("Twenty", 1, percent: 20), ifMatch: "\"1\"");

        var response = await Send(HttpMethod.Put, $"{_rulesPath}/{id}", Body("Thirty", 1, percent: 30), ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        _factory.DiscountRuleStore.Versions.Count(stored => stored.Id == id).ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutRule_WithoutIfMatch_Returns428()
    {
        var id = await CreateRule();

        var response = await Send(HttpMethod.Put, $"{_rulesPath}/{id}", Body("Twenty", 1));

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutRule_UnknownRule_Returns404()
    {
        var response = await Send(HttpMethod.Put, $"{_rulesPath}/{Guid.NewGuid()}", Body("Twenty", 1), ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostDeactivate_Rule_StoresAnInactiveNextVersion()
    {
        var id = await CreateRule();

        var response = await Send(HttpMethod.Post, $"{_rulesPath}/{id}/deactivate", ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.ShouldBe("\"2\"");
        var rule = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        rule.GetProperty("isActive").GetBoolean().ShouldBeFalse();
        rule.GetProperty("name").GetString().ShouldBe("Ten percent");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostDeactivate_StaleVersion_Returns412()
    {
        var id = await CreateRule();

        var response = await Send(HttpMethod.Post, $"{_rulesPath}/{id}/deactivate", ifMatch: "\"7\"");

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("""{ "name": "x", "type": "Percentage", "parameters": { "percent": 0, "appliesTo": "order" } }""", "percent")]
    [InlineData("""{ "name": "x", "type": "Percentage", "parameters": { "percent": 10, "appliesTo": "order", "extra": true } }""", "parameters")]
    [InlineData("""{ "name": "x", "type": "FixedAmount", "parameters": { "amounts": [] } }""", "parameters")]
    [InlineData("""{ "name": "x", "type": "BuyXGetY", "parameters": { "buy": 0, "get": 1 } }""", "parameters")]
    [InlineData("""{ "name": "x", "type": "Tiered", "parameters": { "currency": "BRL", "tiers": [] } }""", "parameters")]
    [InlineData("""{ "name": "x", "type": "Percentage" }""", "parameters")]
    [InlineData("""{ "name": "x", "type": "Mystery", "parameters": {} }""", "type")]
    [InlineData("""{ "type": "Percentage", "parameters": { "percent": 10, "appliesTo": "order" } }""", "name")]
    [InlineData("""{ "name": "x", "type": "Percentage", "parameters": { "percent": 10, "appliesTo": "order" }, "conditions": [ { "kind": "Nope" } ] }""", "conditions")]
    [InlineData("""{ "name": "x", "type": "Percentage", "parameters": { "percent": 10, "appliesTo": "order" }, "conditions": { "kind": "FirstOrderOfClient" } }""", "conditions")]
    [InlineData("""{ "name": "x", "type": "Percentage", "parameters": { "percent": 10, "appliesTo": "order" }, "validFrom": "2026-05-01T00:00:00Z", "validTo": "2026-04-01T00:00:00Z" }""", "validTo")]
    public async Task PostRule_InvalidRule_Returns400ProblemDetailsAndStoresNothing(string body, string mentions)
    {
        var response = await Send(HttpMethod.Post, _rulesPath, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadAsStringAsync(Token)).ShouldContain(mentions);
        _factory.DiscountRuleStore.Versions.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutRule_InvalidParameters_Returns400()
    {
        var id = await CreateRule();

        var response = await Send(HttpMethod.Put, $"{_rulesPath}/{id}", Body("Bad", 1, percent: 500), ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _factory.DiscountRuleStore.Versions.Count(stored => stored.Id == id).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PreviewRule_NoCandidate_EqualsTheQuoteOfTheSameCart()
    {
        await CreateRule();
        var preview = await Send(HttpMethod.Post, $"{_rulesPath}/preview", $$"""{ "clientId": "{{_shopper}}", "currency": "BRL", "items": [ { "sku": "KB-1", "quantity": 2 }, { "sku": "MS-1", "quantity": 1 } ] }""");
        var quote = await SendAs(_shopper, HttpMethod.Post, "/api/v1/orders/quote", _cart);

        preview.StatusCode.ShouldBe(HttpStatusCode.OK);
        var previewed = (await preview.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("quote");
        var quoted = await quote.Content.ReadFromJsonAsync<JsonElement>(Token);
        previewed.GetProperty("discountTotal").GetDecimal().ShouldBe(5.00m);
        previewed.GetRawText().ShouldBe(quoted.GetRawText());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PreviewRule_Candidate_AppliesWithoutStoringIt()
    {
        var preview = await Send(HttpMethod.Post, $"{_rulesPath}/preview", $$"""{ "currency": "BRL", "items": [ { "sku": "MS-1", "quantity": 1 } ], "rule": {{_tenPercent}} }""");

        preview.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>(Token);
        body.GetProperty("quote").GetProperty("discountTotal").GetDecimal().ShouldBe(1m);
        body.GetProperty("applied").GetArrayLength().ShouldBe(1);
        _factory.DiscountRuleStore.Versions.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PreviewRule_InvalidCandidateOrEmptyCart_Returns400()
    {
        var invalidRule = await Send(HttpMethod.Post, $"{_rulesPath}/preview", """{ "items": [ { "sku": "MS-1", "quantity": 1 } ], "rule": { "name": "x", "type": "Percentage", "parameters": {} } }""");
        var emptyCart = await Send(HttpMethod.Post, $"{_rulesPath}/preview", """{ "items": [] }""");

        invalidRule.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        emptyCart.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WritingRules_InvalidatesTheCache_SoTheNextQuoteSeesTheChange()
    {
        var before = await QuoteDiscount();
        var id = await CreateRule();
        var afterCreate = await QuoteDiscount();
        await Send(HttpMethod.Put, $"{_rulesPath}/{id}", Body("Twenty", 1, percent: 20), ifMatch: "\"1\"");
        var afterReplace = await QuoteDiscount();
        await Send(HttpMethod.Post, $"{_rulesPath}/{id}/deactivate");
        var afterDeactivate = await QuoteDiscount();

        before.ShouldBe(0m);
        afterCreate.ShouldBe(5.00m);
        afterReplace.ShouldBe(10.00m);
        afterDeactivate.ShouldBe(0m);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AdminRoutes_SignedInWithoutTheAdminRole_Return403()
    {
        var id = await CreateRule();

        foreach (var (method, path, body) in new (HttpMethod, string, string?)[]
        {
            (HttpMethod.Get, _rulesPath, null),
            (HttpMethod.Post, _rulesPath, _tenPercent),
            (HttpMethod.Get, $"{_rulesPath}/{id}", null),
            (HttpMethod.Put, $"{_rulesPath}/{id}", _tenPercent),
            (HttpMethod.Post, $"{_rulesPath}/{id}/deactivate", null),
            (HttpMethod.Post, $"{_rulesPath}/preview", """{ "items": [ { "sku": "MS-1", "quantity": 1 } ] }"""),
        })
        {
            var response = await SendAs(_shopper, method, path, body, ifMatch: "\"1\"");

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AdminRoutes_Anonymous_Return401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _rulesPath);

        (await _client.SendAsync(request, Token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OpenApi_ListsTheAdminDiscountRuleRoutes()
    {
        var document = await _client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Token);
        var paths = document.GetProperty("paths");

        paths.GetProperty(_rulesPath).TryGetProperty("get", out _).ShouldBeTrue();
        paths.GetProperty(_rulesPath).TryGetProperty("post", out _).ShouldBeTrue();
        paths.GetProperty($"{_rulesPath}/{{id}}").TryGetProperty("put", out _).ShouldBeTrue();
        paths.GetProperty($"{_rulesPath}/{{id}}/deactivate").TryGetProperty("post", out _).ShouldBeTrue();
        paths.GetProperty($"{_rulesPath}/preview").TryGetProperty("post", out _).ShouldBeTrue();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static string Body(string name, int priority, int percent = 10) =>
        $$"""{ "name": "{{name}}", "type": "Percentage", "parameters": { "percent": {{percent}}, "appliesTo": "order" }, "priority": {{priority}}, "stackable": false, "oncePerClient": false }""";

    private async Task<Guid> CreateRule(string name = "Ten percent", int priority = 1)
    {
        var response = await Send(HttpMethod.Post, _rulesPath, Body(name, priority));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
    }

    private async Task<decimal> QuoteDiscount()
    {
        var quote = await SendAs(_shopper, HttpMethod.Post, "/api/v1/orders/quote", _cart);

        return (await quote.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("discountTotal").GetDecimal();
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body = null, string? ifMatch = null) =>
        SendAs("admin-1", method, path, body, ifMatch, "Admin");

    private async Task<HttpResponseMessage> SendAs(string user, HttpMethod method, string path, string? body = null, string? ifMatch = null, string? roles = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.Add(TestAuthHandler.NameHeader, "Ana Souza");

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
