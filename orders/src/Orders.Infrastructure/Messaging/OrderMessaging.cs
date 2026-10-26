using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Orders.Application.Ordering;
using Orders.Contracts;
using Orders.Domain;
using Orders.Infrastructure.Persistence;
using Riok.Mapperly.Abstractions;
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

/// <summary>
/// Publishes the domain events as the versioned contracts of <c>Orders.Contracts</c>. Inside <see cref="OrdersTransaction"/>
/// the message goes to the outbox with the order and leaves for RabbitMQ after the commit. Each carries the
/// <c>traceparent</c> of the current activity and the <c>occurred-at</c> time; the AMQP <c>message-id</c> is the envelope id.
/// </summary>
internal sealed class WolverineOrderEventPublisher(IMessageContext messages) : IOrderEventPublisher
{
    public const string TraceParentHeader = "traceparent";
    public const string OccurredAtHeader = "occurred-at";

    public async Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        switch (domainEvent)
        {
            case OrderPlaced placed:
                await PublishAsync(placed.ToContract(), placed.OccurredAt);
                break;
            case OrderStatusChanged changed:
                await PublishAsync(changed.ToContract(), changed.OccurredAt);
                break;
            default:
                throw new NotSupportedException($"{domainEvent.GetType().Name} has no published contract.");
        }
    }

    private async Task PublishAsync<T>(T message, DateTimeOffset occurredAt)
        where T : class
    {
        var options = new DeliveryOptions().WithHeader(OccurredAtHeader, occurredAt.ToString("O", CultureInfo.InvariantCulture));

        if (Activity.Current?.Id is { } traceParent)
        {
            options.WithHeader(TraceParentHeader, traceParent);
        }

        await messages.PublishAsync(message, options);
    }
}

/// <summary>Domain event → published contract.</summary>
[Mapper]
internal static partial class EventContractMapper
{
    [MapProperty(nameof(OrderPlaced.Total) + "." + nameof(Money.Amount), nameof(OrderPlacedV1.Total))]
    [MapProperty(nameof(OrderPlaced.Total) + "." + nameof(Money.Currency), nameof(OrderPlacedV1.Currency))]
    public static partial OrderPlacedV1 ToContract(this OrderPlaced placed);

    public static partial OrderStatusChangedV1 ToContract(this OrderStatusChanged changed);

    private static string ToText(OrderChannel channel) => channel.ToString().ToLowerInvariant();

    private static string ToText(Currency currency) => currency.Code;
}
