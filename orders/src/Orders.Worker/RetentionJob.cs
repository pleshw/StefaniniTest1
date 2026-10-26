using Orders.Application.Retention;

namespace Orders.Worker;

/// <summary>
/// Runs <see cref="RetentionRunner"/> when the Worker starts and then every <see cref="RetentionOptions.Interval"/>: expired
/// idempotency records and old event log rows go in batches. A failed run is logged and tried again at the next interval.
/// </summary>
public sealed partial class RetentionJob(IServiceScopeFactory scopes, RetentionOptions options, ILogger<RetentionJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<RetentionRunner>().RunAsync(stoppingToken);
                LogRan(logger, result.IdempotencyRequests, result.Events);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }

            try
            {
                await Task.Delay(options.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention deleted {IdempotencyRequests} idempotency records and {Events} event log rows.")]
    private static partial void LogRan(ILogger logger, int idempotencyRequests, int events);

    [LoggerMessage(Level = LogLevel.Error, Message = "The retention run failed; it is tried again at the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
