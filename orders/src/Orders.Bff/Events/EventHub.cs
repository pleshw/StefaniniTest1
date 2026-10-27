using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Orders.Bff.Events;

/// <summary>
/// Fans the orders named by the BFF's queue out to every open stream. Each stream has its own bounded channel: a slow
/// one loses its oldest notifications and never holds the others back.
/// </summary>
internal sealed class EventHub(int capacity)
{
    private readonly ConcurrentDictionary<EventSubscription, byte> _subscriptions = new();

    public int Subscribers => _subscriptions.Count;

    public EventSubscription Subscribe()
    {
        var subscription = new EventSubscription(
            Channel.CreateBounded<Guid>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }),
            this);
        _subscriptions.TryAdd(subscription, 0);

        return subscription;
    }

    public void Notify(Guid orderId)
    {
        foreach (var subscription in _subscriptions.Keys)
        {
            subscription.Writer.TryWrite(orderId);
        }
    }

    internal void Remove(EventSubscription subscription) => _subscriptions.TryRemove(subscription, out _);
}

internal sealed class EventSubscription(Channel<Guid> channel, EventHub hub) : IDisposable
{
    public ChannelReader<Guid> Reader => channel.Reader;

    internal ChannelWriter<Guid> Writer => channel.Writer;

    public void Dispose()
    {
        hub.Remove(this);
        channel.Writer.TryComplete();
    }
}
