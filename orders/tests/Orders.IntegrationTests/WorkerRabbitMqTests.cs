extern alias OrdersWorker;

using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Orders.Contracts;
using OrdersWorker::Orders.Worker;

namespace Orders.IntegrationTests;

/// <summary>
/// Orders.Worker on PostgreSQL and RabbitMQ (Testcontainers): the durable inbox dedupes redeliveries, failures are
/// retried after the (shortened) delays and a message that keeps failing is dead-lettered to orders.dlq.
/// </summary>
public sealed class WorkerRabbitMqTests : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly string[] _retryDelays = ["00:00:00.2", "00:00:00.2", "00:00:00.2"];

    private readonly PostgresFixture _postgres;
    private readonly RabbitMqFixture _rabbit;
    private readonly ScriptedProcessor _processor = new();
    private WebApplicationFactory<OrdersWorker::Program>? _factory;

    public WorkerRabbitMqTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    {
        _postgres = postgres;
        _rabbit = rabbit;
    }

    public async ValueTask InitializeAsync()
    {
        // The queues and the Wolverine inbox are shared by the tests of this class, so each one starts from nothing.
        await _rabbit.PurgeAsync(WorkerMessaging.PlacedQueue, Token);
        await _rabbit.PurgeAsync(WorkerMessaging.DeadLetterQueueName, Token);
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DO $$ BEGIN
              IF to_regclass('wolverine.wolverine_incoming_envelopes') IS NOT NULL THEN
                DELETE FROM wolverine.wolverine_incoming_envelopes;
                DELETE FROM wolverine.wolverine_dead_letters;
              END IF;
            END $$;
            """;
        await command.ExecuteNonQueryAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Consume_SameMessageDeliveredTwice_IsHandledOnce()
    {
        StartWorker();
        var message = Placed(Guid.NewGuid());

        await Publish(message, messageId: message.MessageId);
        await Publish(message, messageId: message.MessageId);

        await _processor.WaitForCallsAsync(1, TimeSpan.FromSeconds(30), Token);
        await Task.Delay(TimeSpan.FromSeconds(2), Token);
        _processor.Calls.ShouldBe(1);
        _processor.Handled.ShouldBe([message.Body.OrderId]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Consume_TransientFailure_SucceedsOnRetryTwo()
    {
        _processor.FailFirst = 2;
        StartWorker();
        var message = Placed(Guid.NewGuid());

        await Publish(message, messageId: message.MessageId);

        await _processor.WaitForCallsAsync(3, TimeSpan.FromSeconds(60), Token);
        await Task.Delay(TimeSpan.FromSeconds(2), Token);
        _processor.Calls.ShouldBe(3);
        _processor.Handled.ShouldBe([message.Body.OrderId]);
        (await _rabbit.CountAsync(WorkerMessaging.DeadLetterQueueName, Token)).ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Consume_PoisonMessage_ReachesTheDeadLetterQueueAfterTheLastRetry()
    {
        _processor.FailFirst = int.MaxValue;
        StartWorker();
        var message = Placed(Guid.NewGuid());

        await Publish(message, messageId: message.MessageId);

        var dead = await _rabbit.WaitForMessagesAsync(WorkerMessaging.DeadLetterQueueName, 1, TimeSpan.FromSeconds(60), Token);
        dead.Count.ShouldBe(1);
        _processor.Calls.ShouldBe(4);
        _processor.Handled.ShouldBeEmpty();
        dead[0].Json.GetProperty("orderId").GetGuid().ShouldBe(message.Body.OrderId);
        dead[0].Header("x-exception-type").ShouldNotBeNull().ShouldContain(nameof(InvalidOperationException));
        dead[0].Header("x-exception-message").ShouldNotBeNull().ShouldContain("boom");
        dead[0].Header("x-attempts").ShouldNotBeNull().ShouldBe("4");
        (await _rabbit.CountAsync(WorkerMessaging.PlacedQueue, Token)).ShouldBe(0);
    }

    private void StartWorker()
    {
        _factory = new WorkerFactory(_postgres.ConnectionString, _rabbit.AmqpUri, _processor);
        _ = _factory.Services;
    }

    private static PlacedMessage Placed(Guid orderId) => new(
        Guid.NewGuid(),
        new OrderPlacedV1(orderId, $"ORD-2026-{orderId.ToString("N")[..12].ToUpperInvariant()}", Guid.NewGuid(), "shop", "BRL", 100m, 1, DateTimeOffset.UtcNow));

    private Task Publish(PlacedMessage message, Guid messageId) => _rabbit.PublishAsync(
        "orders.events",
        "order.placed",
        new
        {
            type = "Orders.Contracts.OrderPlacedV1",
            message_id = messageId.ToString(),
            content_type = "application/json",
            headers = new Dictionary<string, string> { ["occurred-at"] = message.Body.OccurredAt.ToString("O") },
        },
        JsonSerializer.Serialize(message.Body, _json),
        Token);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record PlacedMessage(Guid MessageId, OrderPlacedV1 Body);

    private sealed class ScriptedProcessor : IOrderPlacedProcessor
    {
        private readonly List<Guid> _handled = [];
        private int _calls;

        public int FailFirst { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<Guid> Handled
        {
            get
            {
                lock (_handled)
                {
                    return _handled.ToList();
                }
            }
        }

        public Task ProcessAsync(OrderPlacedV1 message, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);

            if (call <= FailFirst)
            {
                throw new InvalidOperationException("boom");
            }

            lock (_handled)
            {
                _handled.Add(message.OrderId);
            }

            return Task.CompletedTask;
        }

        public async Task WaitForCallsAsync(int calls, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (Calls < calls && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private sealed class WorkerFactory(string postgres, string rabbit, IOrderPlacedProcessor processor) : WebApplicationFactory<OrdersWorker::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Orders", postgres);
            builder.UseSetting("ConnectionStrings:RabbitMq", rabbit);
            builder.UseSetting("Orders:Persistence:AutoCreateMessageStorage", "true");
            builder.UseSetting("Orders:Worker:ScheduledJobPollingInterval", "00:00:00.1");

            for (var index = 0; index < _retryDelays.Length; index++)
            {
                builder.UseSetting($"Orders:Worker:RetryDelays:{index}", _retryDelays[index]);
            }

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IOrderPlacedProcessor>();
                services.AddSingleton(processor);
            });
        }
    }
}
