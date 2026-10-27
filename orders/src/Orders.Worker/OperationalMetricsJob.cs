using Orders.Application.Observability;

namespace Orders.Worker;

public sealed record OperationalMetricsOptions
{
    public const string SectionName = "Orders:Metrics";

    /// <summary>How often the OnHold orders and the dead letter queue are read for the <c>orders.on_hold</c> and <c>rabbitmq.dlq.depth</c> gauges.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Runs <see cref="OperationalMetricsRunner"/> when the Worker starts and then every <see cref="OperationalMetricsOptions.PollInterval"/>.
/// It runs in the Worker only, so the readings are not repeated by every API instance. A probe that fails is logged and
/// its gauge goes quiet until the next round succeeds.
/// </summary>
public sealed partial class OperationalMetricsJob(IServiceScopeFactory scopes, OperationalMetricsOptions options, ILogger<OperationalMetricsJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<OperationalMetricsRunner>().RunAsync(stoppingToken);

                if (!result.OnHoldRead || !result.DeadLettersRead)
                {
                    LogPartial(logger, result.OnHoldRead, result.DeadLettersRead);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }

            try
            {
                await Task.Delay(options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The metrics poll could not read everything (OnHold orders read: {OnHoldRead}, dead letter queue read: {DeadLettersRead}); the gauges of the failed reading report nothing until the next round.")]
    private static partial void LogPartial(ILogger logger, bool onHoldRead, bool deadLettersRead);

    [LoggerMessage(Level = LogLevel.Error, Message = "The metrics poll failed; it is tried again at the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
