extern alias OrdersWorker;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Orders.Application.Ordering;
using Orders.Contracts;
using Orders.Domain;
using Orders.Infrastructure.Persistence;
using OrdersWorker::Orders.Worker;

namespace Orders.IntegrationTests;

/// <summary>
/// Dead letters end to end on PostgreSQL and RabbitMQ (Testcontainers): a message the real Worker gives up on is listed by the
/// real API with its error headers, and replaying it sends it back through orders.events so the Worker handles it once.
/// </summary>
public sealed class DeadLetterRabbitMqTests : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _postgres;
    private readonly RabbitMqFixture _rabbit;
    private readonly ScriptedProcessor _processor = new();
    private readonly string _spyQueue = $"test.replayed.{Guid.NewGuid():N}";
    private WebApplicationFactory<Program>? _api;
    private WebApplicationFactory<OrdersWorker::Program>? _worker;

    public DeadLetterRabbitMqTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    {
        _postgres = postgres;
        _rabbit = rabbit;
    }

    public async ValueTask InitializeAsync()
    {
        await _rabbit.PurgeAsync("orders.worker.placed", Token);
        await _rabbit.PurgeAsync("orders.dlq", Token);
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

        _api = new ApiFactory(_postgres.ConnectionString, _rabbit.AmqpUri);
        _worker = new WorkerFactory(_postgres.ConnectionString, _rabbit.AmqpUri, _processor);
        _ = _api.Services;
        _ = _worker.Services;
        await _rabbit.BindQueueAsync(_spyQueue, "orders.events", "order.placed", Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_worker is not null)
        {
            await _worker.DisposeAsync();
        }

        if (_api is not null)
        {
            await _api.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task List_AMessageTheWorkerGaveUpOn_ShowsTheErrorHeadersAndStaysInTheQueue()
    {
        var (orderId, messageId) = await ParkAMessage();
        var client = _api!.CreateClient();

        var first = await Admin(client, HttpMethod.Get, "/api/v1/admin/dead-letters");
        var second = await Admin(client, HttpMethod.Get, "/api/v1/admin/dead-letters");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var letter = (await first.Content.ReadFromJsonAsync<JsonElement>(Token)).EnumerateArray().ShouldHaveSingleItem();
        letter.GetProperty("messageId").GetString().ShouldBe(messageId);
        letter.GetProperty("messageType").GetString().ShouldBe("Orders.Contracts.OrderPlacedV1");
        letter.GetProperty("exceptionType").GetString().ShouldBe("System.InvalidOperationException");
        letter.GetProperty("exceptionMessage").GetString().ShouldBe("boom");
        letter.GetProperty("attempts").GetInt32().ShouldBe(4);
        JsonDocument.Parse(letter.GetProperty("payload").GetString()!).RootElement.GetProperty("orderId").GetGuid().ShouldBe(orderId);
        (await second.Content.ReadFromJsonAsync<JsonElement>(Token)).GetArrayLength().ShouldBe(1);
        (await _rabbit.CountAsync("orders.dlq", Token)).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task List_PeekedManyTimes_StillHoldsTheMessage()
    {
        await ParkAMessage();
        var client = _api!.CreateClient();

        for (var peek = 0; peek < 25; peek++)
        {
            (await Admin(client, HttpMethod.Get, "/api/v1/admin/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await _rabbit.CountAsync("orders.dlq", Token)).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Replay_AParkedMessage_SendsItBackAndTheWorkerHandlesItOnce()
    {
        var (orderId, messageId) = await ParkAMessage();
        var client = _api!.CreateClient();
        _processor.FailFirst = 0;

        var response = await Admin(client, HttpMethod.Post, $"/api/v1/admin/dead-letters/{messageId}/replay");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var replay = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        replay.GetProperty("replayedBy").GetString().ShouldBe("admin-1");
        replay.GetProperty("exchange").GetString().ShouldBe("orders.events");
        replay.GetProperty("routingKey").GetString().ShouldBe("order.placed");
        await _processor.WaitForCallsAsync(5, TimeSpan.FromSeconds(30), Token);
        await Task.Delay(TimeSpan.FromSeconds(2), Token);
        _processor.Calls.ShouldBe(5);
        _processor.Handled.ShouldBe([orderId]);
        (await _rabbit.CountAsync("orders.dlq", Token)).ShouldBe(0);
        var copies = await _rabbit.GetMessagesAsync(_spyQueue, 5, Token);
        var copy = copies.Single(message => message.Header("x-replayed-by") is not null);
        copy.Header("x-replayed-by").ShouldBe("admin-1");
        copy.Header("x-original-message-id").ShouldBe(messageId);
        copy.MessageId.ShouldBe(replay.GetProperty("newMessageId").GetString());
        copy.MessageId.ShouldNotBe(messageId);
        copy.Header("x-exception-type").ShouldBeNull();
        copy.Header("attempts").ShouldBeNull();
        await using var context = _postgres.NewContext();
        var logged = await context.OrderEventLog.SingleAsync(entry => entry.OrderId == orderId && entry.Type == "DeadLetterReplayed.v1", Token);
        JsonDocument.Parse(logged.Payload).RootElement.GetProperty("replayedBy").GetString().ShouldBe("admin-1");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Replay_UnknownMessageId_Returns404AndLeavesTheQueueAlone()
    {
        await ParkAMessage();

        var response = await Admin(_api!.CreateClient(), HttpMethod.Post, $"/api/v1/admin/dead-letters/{Guid.NewGuid()}/replay");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _rabbit.CountAsync("orders.dlq", Token)).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Replay_AMessageAmongOthers_TakesOnlyThatOne()
    {
        var (_, first) = await ParkAMessage();
        var (_, second) = await ParkAMessage();
        _processor.FailFirst = 0;

        var response = await Admin(_api!.CreateClient(), HttpMethod.Post, $"/api/v1/admin/dead-letters/{second}/replay");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var left = (await (await Admin(_api.CreateClient(), HttpMethod.Get, "/api/v1/admin/dead-letters")).Content.ReadFromJsonAsync<JsonElement>(Token))
            .EnumerateArray().Select(letter => letter.GetProperty("messageId").GetString()).ToList();
        left.ShouldBe([first]);
    }

    /// <summary>Makes the Worker fail four times on a new order's message, so it ends up in orders.dlq.</summary>
    private async Task<(Guid OrderId, string MessageId)> ParkAMessage()
    {
        var callsBefore = _processor.Calls;
        _processor.FailFirst = callsBefore + 4;
        var id = Guid.NewGuid();
        var placed = DateTimeOffset.UtcNow;
        var order = Order.Place(
            id, OrderNumbers.For(id, placed), ClientRef.Create(Guid.NewGuid(), "Ana"), "shopper-1", OrderChannel.Shop,
            [OrderItem.Create("KB-1", "Keyboard", Money.Create(10m, Currency.Create("BRL")), 1, 1)], placed);
        await using (var context = _postgres.NewContext())
        {
            context.Orders.Add(order.ToRecord());
            await context.SaveChangesAsync(Token);
        }

        var messageId = Guid.NewGuid().ToString();
        await _rabbit.PublishAsync(
            "orders.events",
            "order.placed",
            new
            {
                type = "Orders.Contracts.OrderPlacedV1",
                message_id = messageId,
                content_type = "application/json",
                headers = new Dictionary<string, string> { ["occurred-at"] = placed.ToString("O") },
            },
            JsonSerializer.Serialize(new OrderPlacedV1(id, order.Number, order.Client.Id, "shop", "BRL", order.Total.Amount, 1, placed), _json),
            Token);
        await _processor.WaitForCallsAsync(callsBefore + 4, TimeSpan.FromSeconds(60), Token);
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline && await _rabbit.CountAsync("orders.dlq", Token) < 1)
        {
            await Task.Delay(200, Token);
        }

        _processor.FailFirst = 0;

        return (id, messageId);
    }

    private static async Task<HttpResponseMessage> Admin(HttpClient client, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TestAuthHandler.UserHeader, "admin-1");
        request.Headers.Add(TestAuthHandler.RolesHeader, "Admin");

        return await client.SendAsync(request, Token);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class ScriptedProcessor : IOrderPlacedProcessor
    {
        private readonly List<Guid> _handled = [];
        private int _calls;
        private int _failFirst;

        public int FailFirst
        {
            get => Volatile.Read(ref _failFirst);
            set => Volatile.Write(ref _failFirst, value);
        }

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
            if (Interlocked.Increment(ref _calls) <= FailFirst)
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

    private sealed class ApiFactory(string postgres, string rabbit) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Orders", postgres);
            builder.UseSetting("ConnectionStrings:RabbitMq", rabbit);
            builder.UseSetting("Orders:Persistence:AutoCreateMessageStorage", "true");
            builder.ConfigureServices(services => services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
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

            for (var index = 0; index < 3; index++)
            {
                builder.UseSetting($"Orders:Worker:RetryDelays:{index}", "00:00:00.2");
            }

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IOrderPlacedProcessor>();
                services.AddSingleton(processor);
            });
        }
    }
}
