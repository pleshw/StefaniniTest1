using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orders.Bff.Clients.Orders;

namespace Orders.Bff.Events;

/// <summary>
/// One Admin page's stream. It subscribes first, then sends what it missed since <c>Last-Event-ID</c> (a cursor of the
/// event log), then, for each order the queue names, that order's new log entries. Every entry is sent once, with its
/// cursor as the event id and its type as the event name. Orders is read with the signed-in admin's own token.
/// </summary>
internal sealed partial class LiveEventStream(
    IOrdersApiClient orders, EventHub hub, IOptions<EventStreamOptions> options, TimeProvider time, ILogger<LiveEventStream> logger)
{
    public const string HeartbeatEvent = "heartbeat";
    public const string BackfillFailedEvent = "backfill-unavailable";

    private const int _pageSize = 100;
    private const int _rememberedIds = 10_000;

    private readonly EventStreamOptions _options = options.Value;

    public async IAsyncEnumerable<SseItem<string>> ReadAsync(string? lastEventId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var subscription = hub.Subscribe();
        var liveFrom = time.GetUtcNow() - _options.LiveWindow;
        var sent = new SentIds(_rememberedIds);

        if (!string.IsNullOrEmpty(lastEventId))
        {
            var (missed, failed) = await TryReadAsync(lastEventId, orderId: null, cancellationToken);

            if (failed)
            {
                yield return new SseItem<string>("{}", BackfillFailedEvent);
            }

            foreach (var entry in missed.Where(sent.Add))
            {
                yield return ToItem(entry);
            }
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var notified = subscription.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var beat = Task.Delay(_options.HeartbeatInterval, time, cancellationToken);

            if (await Task.WhenAny(notified, beat) == beat || !notified.Result)
            {
                yield return new SseItem<string>("{}", HeartbeatEvent);
                continue;
            }

            var named = new HashSet<Guid>();
            while (subscription.Reader.TryRead(out var orderId))
            {
                named.Add(orderId);
            }

            foreach (var orderId in named)
            {
                var (entries, _) = await TryReadAsync(after: null, orderId, cancellationToken);

                foreach (var entry in entries.Where(entry => entry.OccurredAt >= liveFrom && sent.Add(entry)))
                {
                    yield return ToItem(entry);
                }
            }
        }
    }

    /// <summary>Every page after <paramref name="after"/>, oldest first; a failure keeps what was read and reports it.</summary>
    private async Task<(List<OrderEventDto> Entries, bool Failed)> TryReadAsync(string? after, Guid? orderId, CancellationToken cancellationToken)
    {
        var entries = new List<OrderEventDto>();

        try
        {
            do
            {
                var page = await orders.ListEventsAsync(after, _pageSize, null, orderId, cancellationToken);
                entries.AddRange(page.Items);
                after = page.Next;
            }
            while (after is not null);

            return (entries, false);
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException || Composition.Downstream.IsUnavailable(exception, cancellationToken))
        {
            LogReadFailed(exception, orderId);

            return (entries, true);
        }
    }

    private static SseItem<string> ToItem(OrderEventDto entry) =>
        new(JsonSerializer.Serialize(entry), entry.Type) { EventId = entry.Cursor };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live events: the event log could not be read (order {OrderId}).")]
    private partial void LogReadFailed(Exception exception, Guid? orderId);

    /// <summary>The ids already sent on this stream; the oldest are forgotten past the limit.</summary>
    private sealed class SentIds(int limit)
    {
        private readonly HashSet<long> _ids = [];
        private readonly Queue<long> _order = new();

        public bool Add(OrderEventDto entry)
        {
            if (!_ids.Add(entry.Id))
            {
                return false;
            }

            _order.Enqueue(entry.Id);

            if (_order.Count > limit)
            {
                _ids.Remove(_order.Dequeue());
            }

            return true;
        }
    }
}

internal static class LiveEventStreamExtensions
{
    public static IServiceCollection AddLiveEvents(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(EventStreamOptions.SectionName).Get<EventStreamOptions>() ?? new EventStreamOptions();
        services.Configure<EventStreamOptions>(configuration.GetSection(EventStreamOptions.SectionName));
        services.AddSingleton(new EventHub(options.SubscriberCapacity));
        services.AddScoped<LiveEventStream>();

        if (options.Enabled)
        {
            services.AddHostedService<RabbitMqEventListener>();
        }

        return services;
    }
}
