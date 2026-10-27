using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Orders.Bff.UnitTests.Support;

namespace Orders.Bff.UnitTests;

/// <summary>The Admin page's composed routes: the order inspector, the order list with the OnHold queue, creation and backfill.</summary>
public sealed class AdminEndpointTests : IAsyncLifetime
{
    private const string _id = "6f1c2d3e-0000-4000-8000-000000000001";
    private const string _order = $$"""{"id":"{{_id}}","number":"ORD-1","status":"OnHold","rowVersion":1}""";
    private const string _history = """[{"seq":1,"from":null,"to":"Pending","actor":"shopper"},{"seq":2,"from":"Pending","to":"OnHold","actor":"system:worker"}]""";

    private readonly BffFactory _factory = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _factory.CreateAdminAsync();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task OrderInspector_CombinesOrderHistoryAndItsEvents()
    {
        Route(path => path switch
        {
            $"/api/v1/admin/events" => Json(Events(1, 3, next: null)),
            $"/api/v1/orders/{_id}/history" => Json(_history),
            _ => Json(_order),
        });

        var view = await _admin.GetFromJsonAsync<JsonElement>($"/bff/admin/orders/{_id}", Token);

        view.GetProperty("order").GetProperty("number").GetString().ShouldBe("ORD-1");
        view.GetProperty("history").GetArrayLength().ShouldBe(2);
        view.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("id").GetInt64()).ShouldBe([1L, 2L, 3L]);
        view.GetProperty("events")[0].GetProperty("payload").GetProperty("to").GetString().ShouldBe("OnHold");
        view.GetProperty("partial").GetBoolean().ShouldBeFalse();
        view.GetProperty("unavailable").GetArrayLength().ShouldBe(0);
        var events = _factory.Downstream.Requests.Single(request => request.Uri.AbsolutePath == "/api/v1/admin/events");
        events.Uri.Query.ShouldContain($"orderId={_id}");
        _factory.Downstream.Requests.Count.ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task OrderInspector_ManyEvents_KeepsTheLast50()
    {
        Route(path => path switch
        {
            "/api/v1/admin/events" => null,
            $"/api/v1/orders/{_id}/history" => Json(_history),
            _ => Json(_order),
        });
        _factory.Downstream.Respond = request => request.RequestUri!.AbsolutePath == "/api/v1/admin/events"
            ? Json(request.RequestUri.Query.Contains("after=p2", StringComparison.Ordinal) ? Events(101, 120, next: null) : Events(1, 100, next: "p2"))
            : request.RequestUri.AbsolutePath.EndsWith("/history", StringComparison.Ordinal) ? Json(_history) : Json(_order);

        var view = await _admin.GetFromJsonAsync<JsonElement>($"/bff/admin/orders/{_id}", Token);

        view.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("id").GetInt64()).ShouldBe(Enumerable.Range(71, 50).Select(id => (long)id));
    }

    [Theory]
    [InlineData("history")]
    [InlineData("events")]
    [Trait("Category", "Unit")]
    public async Task OrderInspector_OnePartFails_ReturnsTheRestMarkedPartial(string failing)
    {
        Route(path => path switch
        {
            "/api/v1/admin/events" => failing == "events" ? throw new HttpRequestException("down") : Json(Events(1, 1, next: null)),
            $"/api/v1/orders/{_id}/history" => failing == "history" ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Json(_history),
            _ => Json(_order),
        });

        var response = await _admin.GetAsync($"/bff/admin/orders/{_id}", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        view.GetProperty("order").GetProperty("id").GetString().ShouldBe(_id);
        view.GetProperty("partial").GetBoolean().ShouldBeTrue();
        view.GetProperty("unavailable").EnumerateArray().Select(part => part.GetString()).ShouldBe([failing]);
        view.GetProperty(failing).ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task OrderInspector_OrderNotFound_PassesThe404Through()
    {
        Route(path => path.StartsWith("/api/v1/orders/", StringComparison.Ordinal) && !path.EndsWith("/history", StringComparison.Ordinal)
            ? Json("""{"type":"not-found","status":404}""", HttpStatusCode.NotFound, "application/problem+json")
            : Json("[]"));

        var response = await _admin.GetAsync($"/bff/admin/orders/{_id}", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(Token)).ShouldBe("""{"type":"not-found","status":404}""");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task OrderList_PutsTheOnHoldQueueOldestFirstNextToTheFilteredPage()
    {
        _factory.Downstream.Respond = request => request.RequestUri!.Query.Contains("status=OnHold", StringComparison.Ordinal)
            ? Json("""{"items":[{"id":"00000000-0000-0000-0000-000000000003","number":"ORD-3"},{"id":"00000000-0000-0000-0000-000000000002","number":"ORD-2"}],"next":null}""")
            : Json("""{"items":[{"id":"00000000-0000-0000-0000-000000000009","number":"ORD-9"}],"next":"n2"}""");

        var view = await _admin.GetFromJsonAsync<JsonElement>("/bff/admin/orders?q=ana&limit=10", Token);

        view.GetProperty("onHold").EnumerateArray().Select(order => order.GetProperty("number").GetString()).ShouldBe(["ORD-2", "ORD-3"]);
        view.GetProperty("orders").GetProperty("items")[0].GetProperty("number").GetString().ShouldBe("ORD-9");
        view.GetProperty("orders").GetProperty("next").GetString().ShouldBe("n2");
        view.GetProperty("partial").GetBoolean().ShouldBeFalse();
        var filtered = _factory.Downstream.Requests.Single(request => !request.Uri.Query.Contains("status=OnHold", StringComparison.Ordinal));
        filtered.Uri.Query.ShouldContain("q=ana");
        filtered.Uri.Query.ShouldContain("limit=10");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task OrderList_OnHoldQueueFails_ReturnsThePageMarkedPartial()
    {
        _factory.Downstream.Respond = request => request.RequestUri!.Query.Contains("status=OnHold", StringComparison.Ordinal)
            ? throw new HttpRequestException("down")
            : Json("""{"items":[],"next":null}""");

        var view = await _admin.GetFromJsonAsync<JsonElement>("/bff/admin/orders", Token);

        view.GetProperty("partial").GetBoolean().ShouldBeTrue();
        view.GetProperty("unavailable").EnumerateArray().Select(part => part.GetString()).ShouldBe(["onHold"]);
        view.GetProperty("onHold").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateOrder_ForwardsTheIdempotencyKeyAndReturnsOrdersAnswer()
    {
        _factory.Downstream.Respond = _ =>
        {
            var created = Json(_order, HttpStatusCode.Created);
            created.Headers.Location = new Uri($"/api/v1/orders/{_id}", UriKind.Relative);

            return created;
        };
        const string body = """{"clientId":"00000000-0000-0000-0000-0000000000c1","clientName":"Ana","items":[{"sku":"KB-1","quantity":1}]}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/admin/orders") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", "8d1c2d3e-0000-4000-8000-0000000000bb");

        var response = await _admin.SendAsync(request, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.OriginalString.ShouldBe($"/bff/admin/orders/{_id}");
        var forwarded = _factory.Downstream.Requests.ShouldHaveSingleItem();
        (forwarded.Method, forwarded.Uri.ToString()).ShouldBe((HttpMethod.Post, BffFactory.OrdersAddress + "/api/v1/admin/orders"));
        forwarded.Headers["Idempotency-Key"].ShouldBe("8d1c2d3e-0000-4000-8000-0000000000bb");
        forwarded.Body.ShouldBe(body);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Events_BackfillPassesTheFiltersAndReturnsThePage()
    {
        _factory.Downstream.Respond = _ => Json(Events(5, 6, next: "n"));

        var page = await _admin.GetFromJsonAsync<JsonElement>("/bff/admin/events?after=c4&limit=2&type=OrderStatusChanged.v1", Token);

        page.GetProperty("items").GetArrayLength().ShouldBe(2);
        page.GetProperty("next").GetString().ShouldBe("n");
        var forwarded = _factory.Downstream.Requests.ShouldHaveSingleItem();
        forwarded.Uri.AbsolutePath.ShouldBe("/api/v1/admin/events");
        forwarded.Uri.Query.ShouldContain("after=c4");
        forwarded.Uri.Query.ShouldContain("type=OrderStatusChanged.v1");
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private void Route(Func<string, HttpResponseMessage?> byPath) => _factory.Downstream.Respond = request => byPath(request.RequestUri!.AbsolutePath)!;

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static string Events(int first, int last, string? next) => JsonSerializer.Serialize(new
    {
        items = Enumerable.Range(first, last - first + 1).Select(id => new
        {
            id,
            orderId = _id,
            type = "OrderStatusChanged.v1",
            payload = new { to = "OnHold" },
            occurredAt = DateTimeOffset.UnixEpoch.AddMinutes(id),
        }),
        next,
    });

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
