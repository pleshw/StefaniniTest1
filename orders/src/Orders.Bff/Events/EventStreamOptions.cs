namespace Orders.Bff.Events;

/// <summary>The live Admin feed: the BFF's queue on <c>orders.events</c> and the stream it feeds.</summary>
public sealed record EventStreamOptions
{
    public const string SectionName = "Bff:Events";

    public const string ConnectionStringName = "RabbitMq";

    public const string DefaultConnectionString = "amqp://localhost:5672";

    /// <summary>Off only in tests without a broker: streams then send heartbeats and backfill, nothing live.</summary>
    public bool Enabled { get; init; } = true;

    public string Exchange { get; init; } = "orders.events";

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Notifications kept per open stream; a stream that falls further behind loses the oldest.</summary>
    public int SubscriberCapacity { get; init; } = 1000;

    /// <summary>Live entries older than the stream's start minus this are not sent (they belong to a backfill).</summary>
    public TimeSpan LiveWindow { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
}
