using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orders.Bff.Events;
using Orders.Bff.UnitTests.Support;

namespace Orders.Bff.UnitTests;

/// <summary>
/// <c>GET /bff/admin/events/stream</c>: Server-Sent Events of event-log entries, id = the entry's cursor. With
/// <c>Last-Event-ID</c> the missed entries come first, then the live ones, each once.
/// </summary>
public sealed class LiveEventStreamTests : IAsyncLifetime
{
    private static readonly Guid _order = Guid.Parse("6f1c2d3e-0000-4000-8000-000000000001");

    private readonly BffFactory _factory = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _factory.CreateAdminAsync();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Reconnect_WithLastEventId_BackfillsInOrderThenStreamsLiveEntriesOnce()
    {
        _factory.Downstream.Respond = request => request.RequestUri!.Query.Contains("after=c0", StringComparison.Ordinal)
            ? Json(Page(next: null, Entry(1), Entry(2)))
            : Json(Page(next: null, Entry(2), Entry(3)));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/bff/admin/events/stream");
        request.Headers.Add("Last-Event-ID", "c0");
        using var response = await _admin.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        using var events = new SseReader(await response.Content.ReadAsStreamAsync(cancel.Token));

        var backfilled = new[] { await events.NextAsync(cancel.Token), await events.NextAsync(cancel.Token) };
        await WaitForSubscriber();
        _factory.Services.GetRequiredService<EventHub>().Notify(_order);
        var live = await events.NextAsync(cancel.Token);

        backfilled.Select(e => e.Id).ShouldBe(["c1", "c2"]);
        live.Id.ShouldBe("c3");
        live.Type.ShouldBe("OrderStatusChanged.v1");
        JsonDocument.Parse(live.Data).RootElement.GetProperty("id").GetInt64().ShouldBe(3);
        _factory.Downstream.Requests[0].Uri.Query.ShouldContain("after=c0");
        _factory.Downstream.Requests[^1].Uri.Query.ShouldContain($"orderId={_order}");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Idle_SendsAHeartbeat()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _admin.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/bff/admin/events/stream"), HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        using var events = new SseReader(await response.Content.ReadAsStreamAsync(cancel.Token));

        var first = await events.NextAsync(cancel.Token);

        first.Type.ShouldBe("heartbeat");
        response.Headers.GetValues("X-Accel-Buffering").ShouldBe(["no"]);
        _factory.Downstream.Requests.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Live_EntriesFromBeforeTheStreamOpened_AreNotReplayed()
    {
        _factory.Downstream.Respond = _ => Json(Page(next: null, Entry(1, DateTimeOffset.UtcNow.AddHours(-1)), Entry(2)));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _admin.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/bff/admin/events/stream"), HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        using var events = new SseReader(await response.Content.ReadAsStreamAsync(cancel.Token));
        await WaitForSubscriber();

        _factory.Services.GetRequiredService<EventHub>().Notify(_order);
        var live = await events.NextSkippingHeartbeatsAsync(cancel.Token);

        live.Id.ShouldBe("c2");
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task WaitForSubscriber()
    {
        var hub = _factory.Services.GetRequiredService<EventHub>();

        while (hub.Subscribers == 0)
        {
            await Task.Delay(20, Token);
        }
    }

    private static object Entry(int id, DateTimeOffset? at = null) => new
    {
        id,
        orderId = _order,
        type = "OrderStatusChanged.v1",
        payload = new { to = "Confirmed" },
        occurredAt = at ?? DateTimeOffset.UtcNow,
        cursor = $"c{id}",
    };

    private static string Page(string? next, params object[] items) => JsonSerializer.Serialize(new { items, next });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
