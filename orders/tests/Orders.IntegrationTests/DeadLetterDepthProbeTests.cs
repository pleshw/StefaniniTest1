using Microsoft.Extensions.Configuration;
using Orders.Infrastructure.Messaging;
using RabbitMQ.Client;

namespace Orders.IntegrationTests;

/// <summary>The depth reading behind the <c>rabbitmq.dlq.depth</c> gauge, on RabbitMQ 4 (Testcontainers) with the compose definitions.</summary>
public sealed class DeadLetterDepthProbeTests(RabbitMqFixture rabbit) : IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await rabbit.PurgeAsync(RabbitMqDeadLetters.Queue, Token);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadAsync_EmptyQueue_ReturnsZero()
    {
        (await Probe(rabbit.AmqpUri).ReadAsync(Token)).ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadAsync_MessagesWaiting_ReturnsHowManyAndLeavesThemInTheQueue()
    {
        for (var index = 0; index < 3; index++)
        {
            await rabbit.PublishAsync("orders.dlx", "order.placed", new { message_id = Guid.NewGuid().ToString() }, "{}", Token);
        }

        var probe = Probe(rabbit.AmqpUri);

        (await probe.ReadAsync(Token)).ShouldBe(3);
        (await probe.ReadAsync(Token)).ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadAsync_QueueDoesNotExist_ReturnsNull()
    {
        await DeleteQueue(RabbitMqDeadLetters.Queue);

        try
        {
            (await Probe(rabbit.AmqpUri).ReadAsync(Token)).ShouldBeNull();
        }
        finally
        {
            await DeclareQueue();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadAsync_BrokerUnreachable_ThrowsMessagingUnavailable()
    {
        await Should.ThrowAsync<Orders.Application.DeadLetters.MessagingUnavailableException>(
            () => Probe("amqp://guest:guest@127.0.0.1:1").ReadAsync(Token));
    }

    private static RabbitMqDeadLetterDepth Probe(string amqpUri) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:RabbitMq"] = amqpUri }).Build());

    private async Task DeleteQueue(string queue)
    {
        await using var channel = await OpenChannel();
        await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false, noWait: false, Token);
    }

    /// <summary>Puts the queue back as <c>deploy/rabbitmq/definitions.json</c> declares it, for the tests that follow.</summary>
    private async Task DeclareQueue()
    {
        await using var channel = await OpenChannel();
        await channel.QueueDeclareAsync(
            RabbitMqDeadLetters.Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: Token);
        await channel.QueueBindAsync(RabbitMqDeadLetters.Queue, "orders.dlx", "#", cancellationToken: Token);
    }

    private async Task<IChannel> OpenChannel()
    {
        var connection = await new ConnectionFactory { Uri = new Uri(rabbit.AmqpUri) }.CreateConnectionAsync(Token);

        return await connection.CreateChannelAsync(cancellationToken: Token);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
