using Orders.Application;
using Orders.Application.Confirmation;
using Orders.Contracts;

namespace Orders.Worker;

/// <summary>What the Worker does with an order that was placed.</summary>
public interface IOrderPlacedProcessor
{
    Task ProcessAsync(OrderPlacedV1 message, CancellationToken cancellationToken);
}

/// <summary>
/// Decides a placed order with the confirmation rules (Hold rules put it OnHold, otherwise Confirmed). An order that is not
/// Pending any more is left alone. An order that does not exist is logged and dropped: retrying cannot create it. Anything
/// else throws, so the message is retried and finally dead-lettered.
/// </summary>
public sealed partial class AutomaticConfirmationProcessor(EvaluateConfirmationHandler handler, ILogger<AutomaticConfirmationProcessor> logger)
    : IOrderPlacedProcessor
{
    public async Task ProcessAsync(OrderPlacedV1 message, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new EvaluateConfirmationCommand(message.OrderId), cancellationToken);

        if (result.IsSuccess)
        {
            LogDecided(logger, message.OrderId, result.Value);
        }
        else if (result.Error!.Kind == ErrorKind.NotFound)
        {
            LogUnknownOrder(logger, message.OrderId);
        }
        else
        {
            throw new InvalidOperationException($"Order {message.OrderId} could not be decided: {result.Error.Message}");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} decided: {Outcome}.")]
    private static partial void LogDecided(ILogger logger, Guid orderId, ConfirmationOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order {OrderId} was announced but does not exist; the message is dropped.")]
    private static partial void LogUnknownOrder(ILogger logger, Guid orderId);
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
