using Microsoft.Extensions.Configuration;
using Orders.Contracts;
using Wolverine;
using Wolverine.RabbitMQ;

namespace Orders.Infrastructure.Messaging;

public sealed record OrdersMessagingOptions
{
    public const string SectionName = "Orders:Messaging";

    public const string ConnectionStringName = "RabbitMq";

    public const string DefaultConnectionString = "amqp://localhost:5672";

    /// <summary>Turned off only by tests that start a host without a broker: events then have no route and are dropped.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Declares the exchange (and, in the Worker, the queues) when the host starts. They are idempotent and match deploy/rabbitmq/definitions.json.</summary>
    public bool AutoProvision { get; init; } = true;
}

/// <summary>Names of the RabbitMQ objects Orders publishes to and consumes from; they mirror <c>deploy/rabbitmq/definitions.json</c>.</summary>
public static class OrdersTopology
{
    public const string EventsExchange = "orders.events";

    public const string PlacedRoutingKey = "order.placed";

    public const string StatusChangedRoutingKey = "order.status-changed";
}

public static class OrdersMessagingExtensions
{
    public static OrdersMessagingOptions MessagingOptions(this IConfiguration configuration) =>
        configuration.GetSection(OrdersMessagingOptions.SectionName).Get<OrdersMessagingOptions>() ?? new OrdersMessagingOptions();

    /// <summary>
    /// Routes the event contracts to the topic exchange <c>orders.events</c> through the Wolverine outbox, with publisher
    /// confirms, so a message is deleted from the outbox only after the broker has stored it.
    /// </summary>
    internal static void UseOrdersRabbitMq(this WolverineOptions wolverine, IConfiguration configuration)
    {
        var messaging = configuration.MessagingOptions();

        if (!messaging.Enabled)
        {
            return;
        }

        var connectionString = configuration.GetConnectionString(OrdersMessagingOptions.ConnectionStringName)
            ?? OrdersMessagingOptions.DefaultConnectionString;
        var rabbit = wolverine.UseRabbitMq(new Uri(connectionString));

        if (messaging.AutoProvision)
        {
            rabbit.AutoProvision();
        }

        rabbit.DeclareExchange(OrdersTopology.EventsExchange, exchange =>
        {
            exchange.ExchangeType = ExchangeType.Topic;
            exchange.IsDurable = true;
        });

        wolverine.PublishMessage<OrderPlacedV1>()
            .ToRabbitTopic(OrdersTopology.PlacedRoutingKey, OrdersTopology.EventsExchange)
            .UseDurableOutbox();
        wolverine.PublishMessage<OrderStatusChangedV1>()
            .ToRabbitTopic(OrdersTopology.StatusChangedRoutingKey, OrdersTopology.EventsExchange)
            .UseDurableOutbox();
    }
}
