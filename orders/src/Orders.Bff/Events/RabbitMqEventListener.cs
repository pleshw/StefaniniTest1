using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Orders.Bff.Events;

/// <summary>
/// This instance's own queue on <c>orders.events</c> (<c>#</c>): exclusive, auto-delete and server-named, so it lives as
/// long as the connection and every instance gets every event. Messages are only a signal naming the order; the
/// streams read the event log for the entries. A lost connection is recovered by the client, a failed start retried.
/// </summary>
internal sealed partial class RabbitMqEventListener(
    EventHub hub, IOptions<EventStreamOptions> options, IConfiguration configuration, ILogger<RabbitMqEventListener> logger) : BackgroundService
{
    private readonly EventStreamOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(configuration.GetConnectionString(EventStreamOptions.ConnectionStringName) ?? EventStreamOptions.DefaultConnectionString),
            ClientProvidedName = "orders-bff",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                var queue = await channel.QueueDeclareAsync(string.Empty, durable: false, exclusive: true, autoDelete: true, cancellationToken: stoppingToken);
                await channel.QueueBindAsync(queue.QueueName, _options.Exchange, "#", cancellationToken: stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, message) =>
                {
                    if (EventMessages.OrderIdOf(message.RoutingKey, message.Body) is { } orderId)
                    {
                        hub.Notify(orderId);
                    }

                    return Task.CompletedTask;
                };
                await channel.BasicConsumeAsync(queue.QueueName, autoAck: true, consumer, stoppingToken);
                LogListening(queue.QueueName, _options.Exchange);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogConnectFailed(exception, _options.ReconnectDelay);
                await Task.Delay(_options.ReconnectDelay, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Live events: queue {Queue} bound to {Exchange}.")]
    private partial void LogListening(string queue, string exchange);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live events: could not listen to RabbitMQ; trying again in {Delay}.")]
    private partial void LogConnectFailed(Exception exception, TimeSpan delay);
}
