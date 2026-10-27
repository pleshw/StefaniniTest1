using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Orders.Application;
using Orders.Application.DeadLetters;
using Orders.Application.Observability;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Orders.Infrastructure.Messaging;

/// <summary>
/// The dead letter queue <c>orders.dlq</c> through RabbitMQ.Client (already in the build as the Wolverine transport's own
/// dependency). Peeking is <c>basic.get</c> followed by a requeueing <c>basic.nack</c>, so the messages stay; the queue
/// has a policy without a delivery limit (<c>deploy/rabbitmq/definitions.json</c>), or RabbitMQ 4 would drop a message
/// after 20 peeks. Each operation opens its own connection: these are rare admin calls.
/// </summary>
internal sealed class RabbitMqDeadLetters(IConfiguration configuration) : IDeadLetters
{
    public const string Queue = "orders.dlq";

    public const string ReplayedByHeader = "x-replayed-by";

    public const string OriginalMessageIdHeader = "x-original-message-id";

    /// <summary>The most messages a replay looks through to find its message.</summary>
    private const int _searchLimit = 10_000;

    // Failure details Wolverine and the Worker stamp on a dead letter; a replay starts clean.
    private static readonly string[] _failureHeaderPrefixes = ["exception-", "x-exception-", "attempts", "x-attempts", "failed-at", "failure-", "first-failed-at", "original-destination"];

    public async Task<IReadOnlyList<DeadLetterDto>> PeekAsync(int limit, CancellationToken cancellationToken)
    {
        return await Broker(async channel =>
        {
            var letters = new List<DeadLetterDto>();
            ulong lastTag = 0;

            while (letters.Count < limit && await channel.BasicGetAsync(Queue, autoAck: false, cancellationToken) is { } message)
            {
                lastTag = message.DeliveryTag;
                letters.Add(ToLetter(message));
            }

            if (lastTag != 0)
            {
                await channel.BasicNackAsync(lastTag, multiple: true, requeue: true, cancellationToken);
            }

            return (IReadOnlyList<DeadLetterDto>)letters;
        });
    }

    public async Task<Result<DeadLetterReplay>> ReplayAsync(string messageId, string replayedBy, DeadLetterReplayHooks hooks, CancellationToken cancellationToken)
    {
        return await Broker(async channel =>
        {
            var others = new List<ulong>();
            BasicGetResult? found = null;

            try
            {
                for (var looked = 0; looked < _searchLimit && found is null; looked++)
                {
                    var message = await channel.BasicGetAsync(Queue, autoAck: false, cancellationToken);

                    if (message is null)
                    {
                        break;
                    }

                    if (message.BasicProperties.MessageId == messageId)
                    {
                        found = message;
                    }
                    else
                    {
                        others.Add(message.DeliveryTag);
                    }
                }

                if (found is null)
                {
                    return Result.Fail<DeadLetterReplay>(ErrorKind.NotFound, $"No dead letter has the message id {messageId}.");
                }

                var letter = ToLetter(found);

                if (await hooks.Check(letter, cancellationToken) is { } veto)
                {
                    await channel.BasicNackAsync(found.DeliveryTag, multiple: false, requeue: true, cancellationToken);

                    return Result.Fail<DeadLetterReplay>(veto.Kind, veto.Message);
                }

                var replay = await RepublishAsync(channel, found, letter, replayedBy, cancellationToken);

                try
                {
                    await hooks.Record(replay, cancellationToken);
                }
                catch
                {
                    await channel.BasicNackAsync(found.DeliveryTag, multiple: false, requeue: true, cancellationToken);
                    throw;
                }

                await channel.BasicAckAsync(found.DeliveryTag, multiple: false, cancellationToken);

                return Result.Ok(replay);
            }
            finally
            {
                if (others.Count > 0)
                {
                    await channel.BasicNackAsync(others.Max(), multiple: true, requeue: true, cancellationToken);
                }
            }
        });
    }

    /// <summary>
    /// Publishes a copy under a new message id (the Worker's inbox remembers the old one) to the exchange and routing key
    /// the message type belongs to, with the failure details removed and <c>x-replayed-by</c> added; waits for the broker to confirm.
    /// </summary>
    private static async Task<DeadLetterReplay> RepublishAsync(
        IChannel channel, BasicGetResult message, DeadLetterDto letter, string replayedBy, CancellationToken cancellationToken)
    {
        var (exchange, routingKey) = RouteOf(letter);
        var newMessageId = Guid.NewGuid().ToString();
        var properties = new BasicProperties(message.BasicProperties) { MessageId = newMessageId };
        var headers = (message.BasicProperties.Headers ?? new Dictionary<string, object?>())
            .Where(header => !_failureHeaderPrefixes.Any(prefix => header.Key.StartsWith(prefix, StringComparison.Ordinal)))
            .ToDictionary(header => header.Key, header => header.Value);
        headers[ReplayedByHeader] = replayedBy;
        headers[OriginalMessageIdHeader] = letter.MessageId;
        properties.Headers = headers;

        await channel.BasicPublishAsync(exchange, routingKey, mandatory: true, properties, message.Body, cancellationToken);

        return new DeadLetterReplay(letter, newMessageId, exchange, routingKey, replayedBy);
    }

    /// <summary>The topic exchange a message of this type was published to, and its routing key.</summary>
    private static (string Exchange, string RoutingKey) RouteOf(DeadLetterDto letter) => letter.MessageType switch
    {
        "Orders.Contracts.OrderPlacedV1" => (OrdersTopology.EventsExchange, OrdersTopology.PlacedRoutingKey),
        "Orders.Contracts.OrderStatusChangedV1" => (OrdersTopology.EventsExchange, OrdersTopology.StatusChangedRoutingKey),
        _ => throw new InvalidOperationException($"Message type {letter.MessageType} has no known exchange."),
    };

    private static DeadLetterDto ToLetter(BasicGetResult message)
    {
        var headers = (message.BasicProperties.Headers ?? new Dictionary<string, object?>())
            .ToDictionary(header => header.Key, header => HeaderText(header.Value));
        int? attempts = int.TryParse(headers.GetValueOrDefault("x-attempts") ?? headers.GetValueOrDefault("attempts"), CultureInfo.InvariantCulture, out var count) ? count : null;

        return new DeadLetterDto(
            message.BasicProperties.MessageId ?? string.Empty,
            message.BasicProperties.Type,
            headers.GetValueOrDefault("original-destination"),
            headers.GetValueOrDefault("x-exception-type") ?? headers.GetValueOrDefault("exception-type"),
            headers.GetValueOrDefault("x-exception-message") ?? headers.GetValueOrDefault("exception-message"),
            attempts,
            Encoding.UTF8.GetString(message.Body.Span),
            headers);
    }

    private static string HeaderText(object? value) => value switch
    {
        null => string.Empty,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        string text => text,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private Task<T> Broker<T>(Func<IChannel, Task<T>> work) =>
        RabbitMqBroker.RunAsync(configuration, new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), work);
}

/// <summary>One short-lived connection and channel to the broker of <c>ConnectionStrings:RabbitMq</c>, for the rare admin calls and the metrics poll.</summary>
internal static class RabbitMqBroker
{
    public static async Task<T> RunAsync<T>(IConfiguration configuration, CreateChannelOptions? channelOptions, Func<IChannel, Task<T>> work)
    {
        var connectionString = configuration.GetConnectionString(OrdersMessagingOptions.ConnectionStringName) ?? OrdersMessagingOptions.DefaultConnectionString;
        var factory = new ConnectionFactory { Uri = new Uri(connectionString), RequestedConnectionTimeout = TimeSpan.FromSeconds(5) };

        try
        {
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync(channelOptions);

            return await work(channel);
        }
        catch (Exception exception) when (exception is BrokerUnreachableException or AlreadyClosedException or System.Net.Sockets.SocketException or TimeoutException)
        {
            throw new MessagingUnavailableException("The message broker could not be reached; try again shortly.", exception);
        }
    }
}

/// <summary>
/// How many messages wait in <c>orders.dlq</c>, read with a passive declare: the broker answers with the queue's own count
/// (the management API's totals are sampled and lag behind) and no message is touched. A missing queue is null, not an error.
/// </summary>
internal sealed class RabbitMqDeadLetterDepth(IConfiguration configuration) : IDeadLetterDepthProbe
{
    public async Task<long?> ReadAsync(CancellationToken cancellationToken) =>
        await RabbitMqBroker.RunAsync<long?>(configuration, null, async channel =>
        {
            try
            {
                return (await channel.QueueDeclarePassiveAsync(RabbitMqDeadLetters.Queue, cancellationToken)).MessageCount;
            }
            catch (OperationInterruptedException exception) when (exception.ShutdownReason?.ReplyCode == 404)
            {
                return null;
            }
        });
}

/// <summary>Stands in when messaging is switched off: there is no dead letter queue to measure.</summary>
internal sealed class DisabledDeadLetterDepth : IDeadLetterDepthProbe
{
    public Task<long?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<long?>(null);
}

/// <summary>Stands in when messaging is switched off: there is no dead letter queue to look at.</summary>
internal sealed class DisabledDeadLetters : IDeadLetters
{
    public Task<IReadOnlyList<DeadLetterDto>> PeekAsync(int limit, CancellationToken cancellationToken) =>
        throw new MessagingUnavailableException("Messaging is switched off.", new InvalidOperationException());

    public Task<Result<DeadLetterReplay>> ReplayAsync(string messageId, string replayedBy, DeadLetterReplayHooks hooks, CancellationToken cancellationToken) =>
        throw new MessagingUnavailableException("Messaging is switched off.", new InvalidOperationException());
}
