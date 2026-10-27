using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orders.Application.Ordering;

namespace Orders.IntegrationTests;

/// <summary>The admin event log endpoint through the real Orders.Api pipeline over an in-memory log (no Docker).</summary>
public sealed class AdminEventEndpointTests : IAsyncDisposable
{
    private const string _path = "/api/v1/admin/events";

    private static readonly DateTimeOffset _start = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid _orderA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid _orderB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");

    private readonly OrdersApiFactory _factory = new();
    private readonly HttpClient _client;

    public AdminEventEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_Events_AreListedOldestFirstWithTheirPayload()
    {
        await Log(_orderA, "OrderStatusChanged.v1", minutes: 5);
        await Log(_orderB, "DeadLetterReplayed.v1", minutes: 1);
        await Log(_orderA, "OrderStatusChanged.v1", minutes: 3);

        var page = await Get(_path);

        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("occurredAt").GetDateTimeOffset()).ShouldBe(
            [_start.AddMinutes(1), _start.AddMinutes(3), _start.AddMinutes(5)]);
        var first = page.GetProperty("items")[0];
        first.GetProperty("orderId").GetGuid().ShouldBe(_orderB);
        first.GetProperty("type").GetString().ShouldBe("DeadLetterReplayed.v1");
        first.GetProperty("payload").GetProperty("note").GetString().ShouldBe("DeadLetterReplayed.v1");
        first.GetProperty("id").GetInt64().ShouldBeGreaterThan(0);
        page.GetProperty("next").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_EachEventCarriesTheCursorThatContinuesRightAfterIt()
    {
        for (var minute = 1; minute <= 4; minute++)
        {
            await Log(_orderA, "OrderStatusChanged.v1", minute);
        }

        var second = (await Get(_path)).GetProperty("items")[1];

        var rest = await Get($"{_path}?after={second.GetProperty("cursor").GetString()}");

        rest.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("occurredAt").GetDateTimeOffset())
            .ShouldBe([_start.AddMinutes(3), _start.AddMinutes(4)]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_LimitAndAfter_PageThroughEveryEventOnceInOrder()
    {
        for (var minute = 1; minute <= 5; minute++)
        {
            await Log(_orderA, "OrderStatusChanged.v1", minute);
        }

        var seen = new List<DateTimeOffset>();
        string? after = null;
        var pages = 0;

        do
        {
            var page = await Get($"{_path}?limit=2" + (after is null ? string.Empty : $"&after={after}"));
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("occurredAt").GetDateTimeOffset()));
            after = page.GetProperty("next").GetString();
            pages++;
        }
        while (after is not null);

        pages.ShouldBe(3);
        seen.ShouldBe(Enumerable.Range(1, 5).Select(minute => _start.AddMinutes(minute)).ToList());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_EventsAtTheSameTime_AreOrderedByIdAndNoneIsSkippedOrRepeatedAcrossPages()
    {
        for (var index = 0; index < 5; index++)
        {
            await Log(_orderA, "OrderStatusChanged.v1", minutes: 1);
        }

        var ids = new List<long>();
        string? after = null;

        do
        {
            var page = await Get($"{_path}?limit=2" + (after is null ? string.Empty : $"&after={after}"));
            ids.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()));
            after = page.GetProperty("next").GetString();
        }
        while (after is not null);

        ids.ShouldBe(ids.Order().ToList());
        ids.Distinct().Count().ShouldBe(5);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_TypeAndOrderIdFilters_NarrowTheList()
    {
        await Log(_orderA, "OrderStatusChanged.v1", 1);
        await Log(_orderA, "DeadLetterReplayed.v1", 2);
        await Log(_orderB, "OrderStatusChanged.v1", 3);

        var byType = await Get($"{_path}?type=OrderStatusChanged.v1");
        var byOrder = await Get($"{_path}?orderId={_orderA}");
        var both = await Get($"{_path}?orderId={_orderA}&type=OrderStatusChanged.v1");
        var none = await Get($"{_path}?type=Nope.v1");

        byType.GetProperty("items").GetArrayLength().ShouldBe(2);
        byOrder.GetProperty("items").GetArrayLength().ShouldBe(2);
        both.GetProperty("items").GetArrayLength().ShouldBe(1);
        none.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("after=garbage")]
    [InlineData("orderId=00000000-0000-0000-0000-000000000000")]
    public async Task Get_InvalidParameters_Return400ProblemDetails(string query)
    {
        var response = await Send($"{_path}?{query}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_AdminActionsAreInTheLog()
    {
        using var place = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent("""{"currency":"BRL","items":[{"sku":"KB-1","quantity":1}]}""", System.Text.Encoding.UTF8, "application/json"),
        };
        place.Headers.Add(TestAuthHandler.UserHeader, "11111111-1111-1111-1111-111111111111");
        place.Headers.Add(TestAuthHandler.NameHeader, "Ana");
        place.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var order = (await (await _client.SendAsync(place, Token)).Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
        using var hold = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/orders/{order}/hold") { Content = JsonContent.Create(new { reason = "check" }) };
        hold.Headers.Add(TestAuthHandler.UserHeader, "admin-1");
        hold.Headers.Add(TestAuthHandler.RolesHeader, "Admin");
        hold.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        (await _client.SendAsync(hold, Token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var page = await Get($"{_path}?orderId={order}&type=OrderStatusChanged.v1");

        var item = page.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("type").GetString().ShouldBe("OrderStatusChanged.v1");
        item.GetProperty("payload").GetProperty("to").GetString().ShouldBe("OnHold");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_SignedInWithoutTheAdminRole_Returns403_AndAnonymousReturns401()
    {
        using var shopper = new HttpRequestMessage(HttpMethod.Get, _path);
        shopper.Headers.Add(TestAuthHandler.UserHeader, "11111111-1111-1111-1111-111111111111");
        using var anonymous = new HttpRequestMessage(HttpMethod.Get, _path);

        (await _client.SendAsync(shopper, Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _client.SendAsync(anonymous, Token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OpenApi_ListsTheRoute()
    {
        var paths = (await _client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Token)).GetProperty("paths");

        paths.GetProperty(_path).TryGetProperty("get", out _).ShouldBeTrue();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private Task Log(Guid orderId, string type, int minutes) =>
        _factory.EventLog.AppendEntryAsync(new OrderEventLogEntry(orderId, type, $$"""{ "note": "{{type}}" }""", _start.AddMinutes(minutes)), Token);

    private async Task<JsonElement> Get(string path)
    {
        var response = await Send(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private async Task<HttpResponseMessage> Send(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestAuthHandler.UserHeader, "admin-1");
        request.Headers.Add(TestAuthHandler.RolesHeader, "Admin");

        return await _client.SendAsync(request, Token);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
