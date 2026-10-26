using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Orders.Application.Ordering;
using Orders.Domain;
using Orders.Infrastructure.Persistence;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Orders.Infrastructure.Messaging;

/// <summary>
/// One PostgreSQL transaction for the idempotency row, the order and the outbox message: Wolverine writes the message
/// to its outbox table on the same connection and sends it only after the commit.
/// </summary>
internal sealed class OrdersTransaction(OrdersDbContext context, IMessageContext messages, IWolverineRuntime runtime) : IOrderTransaction
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        if (messages is not MessageContext bus || runtime.Storage is not IMessageDatabase database)
        {
            throw new InvalidOperationException("Wolverine PostgreSQL message persistence is not configured.");
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await bus.EnlistInOutboxAsync(new DatabaseEnvelopeTransaction(database, transaction.GetDbTransaction()));

        var result = await work(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await bus.FlushOutgoingMessagesAsync();

        return result;
    }
}

internal sealed class WolverineOrderEventPublisher(IMessageContext messages) : IOrderEventPublisher
{
    public async Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken) =>
        await messages.PublishAsync(domainEvent);
}

/// <summary>
/// Consumes the durable local queue that receives <see cref="OrderPlaced"/> until RabbitMQ routing arrives (C31).
/// It only records that the event was delivered.
/// </summary>
public sealed partial class OrderPlacedHandler(ILogger<OrderPlacedHandler> logger)
{
    public const string LocalQueue = "orders-events";

    public void Handle(OrderPlaced message) => LogOrderPlaced(logger, message.OrderId, message.Number);

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} ({OrderNumber}) placed.")]
    private static partial void LogOrderPlaced(ILogger logger, Guid orderId, string orderNumber);
}
