using Microsoft.Extensions.DependencyInjection.Extensions;
using Orders.Infrastructure.Messaging;
using Orders.Infrastructure.Persistence;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;

namespace Orders.Worker;

public sealed record WorkerOptions
{
    public const string SectionName = "Orders:Worker";

    private static readonly TimeSpan[] _defaultRetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];

    /// <summary>
    /// Waits before the first, second and third retry; one more failure dead-letters the message. Set in
    /// appsettings.json, where tests shorten them. No default is written here: the configuration binder would append
    /// to it instead of replacing it.
    /// </summary>
    public TimeSpan[] RetryDelays { get; init; } = [];

    public IReadOnlyList<TimeSpan> EffectiveRetryDelays => RetryDelays.Length > 0 ? RetryDelays : _defaultRetryDelays;

    /// <summary>
    /// How often the durable inbox looks for retries that are due. A retry never runs sooner than this after its delay
    /// ends, so it bounds how precise the delays are. Tests shorten it.
    /// </summary>
    public TimeSpan ScheduledJobPollingInterval { get; init; } = TimeSpan.FromSeconds(5);
}

public static class WorkerMessaging
{
    public const string PlacedQueue = "orders.worker.placed";

    public const string DeadLetterExchangeName = "orders.dlx";

    public const string DeadLetterQueueName = "orders.dlq";

    public const string ExceptionTypeHeader = "x-exception-type";

    public const string ExceptionMessageHeader = "x-exception-message";

    public const string AttemptsHeader = "x-attempts";

    /// <summary>
    /// The Worker's Wolverine setup on top of the shared persistence and publishing: a quorum queue bound to
    /// <c>orders.events</c> with routing key <c>order.placed</c>, listened to through the durable inbox; failures are
    /// retried after the configured delays and then dead-lettered to <c>orders.dlq</c> through <c>orders.dlx</c>.
    /// </summary>
    public static IServiceCollection AddOrdersWorker(this IServiceCollection services, IConfiguration configuration)
    {
        var worker = configuration.GetSection(WorkerOptions.SectionName).Get<WorkerOptions>() ?? new WorkerOptions();
        services.TryAddSingleton<IOrderPlacedProcessor, LoggingOrderPlacedProcessor>();

        return services.AddOrdersPersistence(configuration, wolverine =>
        {
            wolverine.Discovery.IncludeAssembly(typeof(OrderPlacedConsumer).Assembly);
            wolverine.Durability.ScheduledJobPollingTime = worker.ScheduledJobPollingInterval;
            wolverine.OnException<Exception>()
                .ScheduleRetry([.. worker.EffectiveRetryDelays])
                .Then.CustomAction(DeadLetterWithErrorHeadersAsync, "Dead-letter to orders.dlq with the error headers");

            if (!configuration.MessagingOptions().Enabled)
            {
                return;
            }

            // The dead letter exchange and queue are declared exactly as deploy/rabbitmq/definitions.json declares them (a
            // topic exchange binding "#" to a quorum queue): RabbitMQ refuses a redeclaration with other arguments.
            wolverine.ConfigureRabbitMq()
                .DisableDeadLetterQueueing()
                .DeclareExchange(OrdersTopology.EventsExchange, exchange => exchange.BindQueue(PlacedQueue, OrdersTopology.PlacedRoutingKey))
                .DeclareExchange(DeadLetterExchangeName, exchange =>
                {
                    exchange.ExchangeType = ExchangeType.Topic;
                    exchange.IsDurable = true;
                    exchange.BindQueue(DeadLetterQueueName, "#");
                })
                .DeclareQueue(DeadLetterQueueName, queue => queue.QueueType = QueueType.quorum);
            wolverine.ListenToRabbitQueue(PlacedQueue, queue => queue.QueueType = QueueType.quorum)
                .DeadLetterQueueing(new DeadLetterQueue(DeadLetterQueueName) { ExchangeName = DeadLetterExchangeName })
                .UseDurableInbox();
        });
    }

    /// <summary>Stamps the failure on the message, then moves it to the dead letter queue; the headers travel with it to <c>orders.dlq</c>.</summary>
    private static async ValueTask DeadLetterWithErrorHeadersAsync(IWolverineRuntime runtime, IEnvelopeLifecycle lifecycle, Exception exception)
    {
        var envelope = lifecycle.Envelope ?? throw new InvalidOperationException("The failed message has no envelope.");
        envelope.Headers[ExceptionTypeHeader] = exception.GetType().FullName ?? exception.GetType().Name;
        envelope.Headers[ExceptionMessageHeader] = exception.Message;
        envelope.Headers[AttemptsHeader] = envelope.Attempts.ToString(System.Globalization.CultureInfo.InvariantCulture);

        await lifecycle.MoveToDeadLetterQueueAsync(exception);
    }
}
