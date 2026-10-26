using System.Text.Json;
using Orders.Application.Ordering;
using Orders.Domain;
using Orders.Infrastructure.Messaging;

namespace Orders.Infrastructure.Persistence;

/// <summary>
/// Appends to <c>order_event_log</c> on the order transaction's connection. The payload is the published contract
/// (<c>OrderStatusChanged.v1</c>), so what the log shows is what consumers received.
/// </summary>
internal sealed class OrderEventLog(OrdersDbContext context) : IOrderEventLog
{
    public const string StatusChangedType = "OrderStatusChanged.v1";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task AppendAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var entry = domainEvent switch
        {
            OrderStatusChanged changed => new OrderEventLogEntry(
                changed.OrderId, StatusChangedType, JsonSerializer.Serialize(changed.ToContract(), _json), changed.OccurredAt),
            _ => throw new NotSupportedException($"{domainEvent.GetType().Name} is not written to the event log."),
        };

        await AppendEntryAsync(entry, cancellationToken);
    }

    public async Task AppendEntryAsync(OrderEventLogEntry entry, CancellationToken cancellationToken)
    {
        context.OrderEventLog.Add(new OrderEventLogRecord { OrderId = entry.OrderId, Type = entry.Type, Payload = entry.Payload, OccurredAt = entry.OccurredAt });
        await context.SaveChangesAsync(cancellationToken);
    }
}
