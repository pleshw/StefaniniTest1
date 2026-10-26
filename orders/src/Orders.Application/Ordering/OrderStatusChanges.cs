using Orders.Domain;

namespace Orders.Application.Ordering;

/// <summary>Append-only record of what happened to orders (<c>order_event_log</c>), written in the same transaction as the change.</summary>
public interface IOrderEventLog
{
    Task AppendAsync(IDomainEvent domainEvent, CancellationToken cancellationToken);

    /// <summary>Appends something that is not a status change, such as a dead letter replay.</summary>
    Task AppendEntryAsync(OrderEventLogEntry entry, CancellationToken cancellationToken);
}

/// <param name="Type">A versioned name such as <c>DeadLetterReplayed.v1</c>.</param>
/// <param name="Payload">JSON.</param>
public sealed record OrderEventLogEntry(Guid OrderId, string Type, string Payload, DateTimeOffset OccurredAt);

/// <summary>
/// Records the events an order raised while it changed status: one <c>order_event_log</c> row and one outbox message
/// (<c>OrderStatusChanged.v1</c>) each. Call it inside <see cref="IOrderTransaction"/>, after the order was saved.
/// </summary>
public sealed class OrderStatusChangeRecorder(IOrderEventLog log, IOrderEventPublisher events)
{
    public async Task RecordAsync(Order order, CancellationToken cancellationToken)
    {
        foreach (var domainEvent in order.DomainEvents)
        {
            await log.AppendAsync(domainEvent, cancellationToken);
            await events.PublishAsync(domainEvent, cancellationToken);
        }

        order.ClearDomainEvents();
    }
}
