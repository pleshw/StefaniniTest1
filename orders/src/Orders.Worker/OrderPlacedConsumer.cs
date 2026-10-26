using Orders.Contracts;

namespace Orders.Worker;

/// <summary>What the Worker does with an order that was placed. C33 replaces the logging default with the confirmation rules.</summary>
public interface IOrderPlacedProcessor
{
    Task ProcessAsync(OrderPlacedV1 message, CancellationToken cancellationToken);
}

internal sealed partial class LoggingOrderPlacedProcessor(ILogger<LoggingOrderPlacedProcessor> logger) : IOrderPlacedProcessor
{
    public Task ProcessAsync(OrderPlacedV1 message, CancellationToken cancellationToken)
    {
        LogOrderPlaced(logger, message.OrderId, message.Number);

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} ({OrderNumber}) placed.")]
    private static partial void LogOrderPlaced(ILogger logger, Guid orderId, string orderNumber);
}

/// <summary>
/// Handles <see cref="OrderPlacedV1"/> from the quorum queue <see cref="WorkerMessaging.PlacedQueue"/>. Wolverine's
/// durable inbox stores each message by its id before the handler runs, so a redelivery of the same message is not
/// handled twice. Any exception goes to the retry policy of <see cref="WorkerMessaging"/>.
/// </summary>
public sealed class OrderPlacedConsumer
{
    public static Task Handle(OrderPlacedV1 message, IOrderPlacedProcessor processor, CancellationToken cancellationToken) =>
        processor.ProcessAsync(message, cancellationToken);
}
