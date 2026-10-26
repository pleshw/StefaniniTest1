using Microsoft.EntityFrameworkCore;
using Orders.Application.Idempotency;

namespace Orders.Infrastructure.Persistence;

internal sealed class IdempotencyStore(OrdersDbContext context, TimeProvider clock) : IIdempotencyStore
{
    private const string _pendingBody = "{}";

    private static readonly TimeSpan _retention = TimeSpan.FromHours(24);

    public async Task<IdempotencyRecord?> TryGetAsync(Guid clientId, Guid key, CancellationToken cancellationToken)
    {
        var record = await context.IdempotencyRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(request => request.ClientId == clientId && request.IdempotencyKey == key, cancellationToken);

        return record is null
            ? null
            : new IdempotencyRecord(record.ClientId, record.IdempotencyKey, record.RequestHash, record.ResponseStatus, record.ResponseBody, record.OrderId);
    }

    public async Task<bool> TryReserveAsync(Guid clientId, Guid key, byte[] requestHash, Guid orderId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var expiresAt = now + _retention;

        var inserted = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO idempotency_requests (client_id, idempotency_key, request_hash, response_status, response_body, order_id, created_at, expires_at)
            VALUES ({clientId}, {key}, {requestHash}, 0, CAST({_pendingBody} AS jsonb), {orderId}, {now}, {expiresAt})
            ON CONFLICT (client_id, idempotency_key) DO NOTHING
            """,
            cancellationToken);

        return inserted == 1;
    }

    public async Task CompleteAsync(Guid clientId, Guid key, int responseStatus, string responseBody, CancellationToken cancellationToken)
    {
        var updated = await context.IdempotencyRequests
            .Where(request => request.ClientId == clientId && request.IdempotencyKey == key)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(request => request.ResponseStatus, (short)responseStatus)
                    .SetProperty(request => request.ResponseBody, responseBody),
                cancellationToken);

        if (updated != 1)
        {
            throw new InvalidOperationException("The idempotency key was not reserved before it was completed.");
        }
    }
}
