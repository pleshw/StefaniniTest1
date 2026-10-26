using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orders.Application.Events;
using Orders.Application.Retention;

namespace Orders.Infrastructure.Persistence;

/// <summary>
/// Reads <c>order_event_log</c> oldest first by <c>(occurred_at, id)</c>, the order of <c>ix_order_event_log_occurred</c>, so a
/// page is an index range scan from the cursor. The order filter uses <c>ix_order_event_log_order</c>.
/// </summary>
internal sealed class OrderEventLogQueries(OrdersDbContext context) : IOrderEventLogQueries
{
    public async Task<IReadOnlyList<OrderEventDto>> ListAsync(EventFilter filter, CancellationToken cancellationToken)
    {
        var events = context.OrderEventLog.AsNoTracking();

        if (filter.OrderId is { } orderId)
        {
            events = events.Where(entry => entry.OrderId == orderId);
        }

        if (filter.Type is { } type)
        {
            events = events.Where(entry => entry.Type == type);
        }

        if (filter.After is { } after)
        {
            events = events.Where(entry => entry.OccurredAt > after.OccurredAt || (entry.OccurredAt == after.OccurredAt && entry.Id > after.Id));
        }

        var rows = await events
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Id)
            .Take(filter.Take)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    private static OrderEventDto ToDto(OrderEventLogRecord row)
    {
        using var payload = JsonDocument.Parse(row.Payload);

        return new OrderEventDto(row.Id, row.OrderId, row.Type, payload.RootElement.Clone(), row.OccurredAt);
    }
}

/// <summary>Deletes expired rows with one DELETE per batch, picking the oldest rows through the expiry and time indexes.</summary>
internal sealed class RetentionStore(OrdersDbContext context) : IRetentionStore
{
    public async Task<int> DeleteExpiredIdempotencyAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM idempotency_requests
            WHERE (client_id, idempotency_key) IN (
                SELECT client_id, idempotency_key FROM idempotency_requests WHERE expires_at < {now} ORDER BY expires_at LIMIT {batchSize})
            """,
            cancellationToken);

    public async Task<int> DeleteEventsOlderThanAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM order_event_log
            WHERE id IN (SELECT id FROM order_event_log WHERE occurred_at < {cutoff} ORDER BY occurred_at, id LIMIT {batchSize})
            """,
            cancellationToken);
}
