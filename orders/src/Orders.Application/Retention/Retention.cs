namespace Orders.Application.Retention;

public sealed record RetentionOptions
{
    public const string SectionName = "Orders:Retention";

    /// <summary>Event log rows older than this are deleted.</summary>
    public TimeSpan EventLogRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>The most rows one DELETE removes, so a large backlog never locks the tables for long.</summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>How often the Worker runs the cleanup.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>Deletes expired rows, one batch at a time; each method returns how many rows that batch removed.</summary>
public interface IRetentionStore
{
    /// <summary>Idempotency records whose <c>expires_at</c> (creation time plus 24 hours) is before <paramref name="now"/>.</summary>
    Task<int> DeleteExpiredIdempotencyAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);

    /// <summary>Event log rows that occurred before <paramref name="cutoff"/>.</summary>
    Task<int> DeleteEventsOlderThanAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken);
}

public sealed record RetentionResult(int IdempotencyRequests, int Events);

/// <summary>One cleanup pass: expired idempotency records and old event log rows, in batches until a batch is not full.</summary>
public sealed class RetentionRunner(IRetentionStore store, TimeProvider clock, RetentionOptions options)
{
    public async Task<RetentionResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var idempotency = await InBatches(batch => store.DeleteExpiredIdempotencyAsync(now, batch, cancellationToken), cancellationToken);
        var events = await InBatches(batch => store.DeleteEventsOlderThanAsync(now - options.EventLogRetention, batch, cancellationToken), cancellationToken);

        return new RetentionResult(idempotency, events);
    }

    private async Task<int> InBatches(Func<int, Task<int>> deleteBatch, CancellationToken cancellationToken)
    {
        var total = 0;
        int deleted;

        do
        {
            deleted = await deleteBatch(options.BatchSize);
            total += deleted;
            cancellationToken.ThrowIfCancellationRequested();
        }
        while (deleted >= options.BatchSize);

        return total;
    }
}
