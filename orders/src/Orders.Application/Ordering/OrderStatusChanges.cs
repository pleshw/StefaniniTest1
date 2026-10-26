using Orders.Domain;

namespace Orders.Application.Ordering;

/// <summary>Append-only record of what happened to orders (<c>order_event_log</c>), written in the same transaction as the change.</summary>
public interface IOrderEventLog
{
    Task AppendAsync(IDomainEvent domainEvent, CancellationToken cancellationToken);
}

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
