namespace Orders.Application.Idempotency;

/// <param name="ResponseStatus">0 while the key is reserved and the request has not completed.</param>
public sealed record IdempotencyRecord(Guid ClientId, Guid Key, byte[] RequestHash, int ResponseStatus, string ResponseBody, Guid OrderId);

/// <summary>Stores the outcome of a create request per client and key, so a retry replays it instead of creating twice.</summary>
public interface IIdempotencyStore
{
    /// <summary>Fast path: the stored record for the key, or null when the key is new.</summary>
    Task<IdempotencyRecord?> TryGetAsync(Guid clientId, Guid key, CancellationToken cancellationToken);

    /// <summary>
    /// Reserves the key for <paramref name="orderId"/>, expiring after 24 hours. Returns false when the key exists
    /// already. Inside a transaction a concurrent duplicate waits for the first one to commit, then gets false.
    /// </summary>
    Task<bool> TryReserveAsync(Guid clientId, Guid key, byte[] requestHash, Guid orderId, CancellationToken cancellationToken);

    /// <summary>Stores the response a reserved key replays.</summary>
    Task CompleteAsync(Guid clientId, Guid key, int responseStatus, string responseBody, CancellationToken cancellationToken);
}
