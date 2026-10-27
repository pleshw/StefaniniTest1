extern alias OrdersWorker;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orders.Domain;
using Orders.Domain.Confirmation;
using Orders.Infrastructure.Persistence;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Orders.IntegrationTests;

/// <summary>
/// Messaging resilience end to end: the real Orders.Api and Orders.Worker on PostgreSQL and RabbitMQ (Testcontainers),
/// a WireMock Catalog. The Worker is stopped and started, the broker is restarted, and an admin races the Worker.
/// </summary>
public sealed class MessagingResilienceTests : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private const string _pricesBody = """
        { "items": [ { "sku": "KB-1", "name": "Keyboard", "category": "peripherals", "unitPrice": 100.00, "currency": "BRL", "version": 3, "isActive": true } ], "missing": [] }
        """;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private readonly PostgresFixture _postgres;
    private readonly RabbitMqFixture _rabbit;
    private readonly WireMockServer _catalog = WireMockServer.Start();
    private readonly string _placedSpy = $"test.resilience-placed.{Guid.NewGuid():N}";
    private readonly string _changedSpy = $"test.resilience-changed.{Guid.NewGuid():N}";
    private readonly string _shopper = Guid.NewGuid().ToString();
    private readonly List<IAsyncDisposable> _hosts = [];
    private WebApplicationFactory<Program> _api = null!;

    public MessagingResilienceTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    {
        _postgres = postgres;
        _rabbit = rabbit;
    }

    public async ValueTask InitializeAsync()
    {
        _catalog.Given(Request.Create().WithPath("/api/v1/products/prices:query").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(_pricesBody));
        await _rabbit.PurgeAsync("orders.worker.placed", Token);
        await using var context = _postgres.NewContext();

        // Rules and the Wolverine inbox are shared by the tests of this class, so each one starts from nothing.
        await context.ConfirmationRules.ExecuteUpdateAsync(set => set.SetProperty(rule => rule.IsActive, false), Token);
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

        _api = Start(new ApiFactory(_postgres.ConnectionString, _rabbit.AmqpUri, _catalog.Url!));
        await _rabbit.BindQueueAsync(_placedSpy, "orders.events", "order.placed", Token);
        await _rabbit.BindQueueAsync(_changedSpy, "orders.events", "order.status-changed", Token);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }

        _catalog.Dispose();
    }

    [Fact]
    [Trait("Category", "Resilience")]
    public async Task WorkerStoppedWhileOrdersArePlaced_AfterItStartsAgain_EveryOrderIsProcessedOnce()
    {
        // The first start declares orders.worker.placed and its binding; the queue then keeps what arrives while no Worker runs.
        var worker = Start(new WorkerFactory(_postgres.ConnectionString, _rabbit.AmqpUri));
        await worker.DisposeAsync();
        _hosts.Remove(worker);
        var client = _api.CreateClient();

        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await PlaceOrder(client));
        }

        await WaitUntil(async () => await _rabbit.CountAsync("orders.worker.placed", Token) == ids.Count);
        (await Statuses(ids)).ShouldAllBe(status => status == OrderStatus.Pending);

        Start(new WorkerFactory(_postgres.ConnectionString, _rabbit.AmqpUri));

        await WaitUntil(async () => (await Statuses(ids)).All(status => status == OrderStatus.Confirmed));
        await AssertProcessedOnce(ids, "system:worker");
    }

    [Fact]
    [Trait("Category", "Resilience")]
    public async Task BrokerRestartedMidFlow_TheOutboxDeliversAfterReconnecting()
    {
        Start(new WorkerFactory(_postgres.ConnectionString, _rabbit.AmqpUri));
        var client = _api.CreateClient();
        var before = await PlaceOrder(client);
        await WaitUntil(async () => (await Statuses([before]))[0] == OrderStatus.Confirmed);
        await _rabbit.GetMessagesAsync(_placedSpy, 10, Token);
        await _rabbit.GetMessagesAsync(_changedSpy, 10, Token);

        await _rabbit.StopBrokerAsync(Token);
        var ids = new List<Guid>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                ids.Add(await PlaceOrder(client));
            }
        }
        finally
        {
            await _rabbit.StartBrokerAsync(Token);
        }

        var placed = await _rabbit.WaitForMessagesAsync(_placedSpy, ids.Count, _timeout, Token);
        placed.Select(message => message.Json.GetProperty("orderId").GetGuid()).Distinct().Order().ShouldBe(ids.Order());
        await WaitUntil(async () => (await Statuses(ids)).All(status => status == OrderStatus.Confirmed));
        await AssertProcessedOnce(ids, "system:worker");
    }

    [Fact]
    [Trait("Category", "Resilience")]
    public async Task AdminConfirmRacesTheHoldRule_EachOrderEndsInExactlyOneState()
    {
        await InsertHoldRule();
        Start(new WorkerFactory(_postgres.ConnectionString, _rabbit.AmqpUri));
        var client = _api.CreateClient();

        var races = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            var id = await PlaceOrder(client);
            var response = await Confirm(client, id);

            return (Id: id, response.StatusCode);
        }));

        var ids = races.Select(race => race.Id).ToList();
        await WaitUntil(async () => (await Statuses(ids)).All(status => status != OrderStatus.Pending));
        await using var context = _postgres.NewContext();

        foreach (var (id, status) in races)
        {
            var order = await context.Orders.Include(record => record.History).SingleAsync(record => record.Id == id, Token);
            var change = order.History.OrderBy(history => history.Seq).Last();

            if (status == HttpStatusCode.OK)
            {
                (order.Status, change.Actor).ShouldBe((OrderStatus.Confirmed, "admin-1"));
            }
            else
            {
                status.ShouldBeOneOf(HttpStatusCode.PreconditionFailed, HttpStatusCode.Conflict);
                (order.Status, change.Actor, change.Reason).ShouldBe((OrderStatus.OnHold, "system:worker", "Hold every BRL order"));
            }
        }

        await AssertProcessedOnce(ids, actor: null);
    }

    [Fact]
    [Trait("Category", "Resilience")]
    public void EveryEventSender_OfTheApiAndTheWorker_GoesThroughTheDurableOutbox()
    {
        var worker = Start(new WorkerFactory(_postgres.ConnectionString, _rabbit.AmqpUri));

        foreach (var services in new[] { _api.Services, worker.Services })
        {
            var senders = services.GetRequiredService<IWolverineRuntime>().Options.Transports.AllEndpoints()
                .Where(endpoint => endpoint.Uri.Scheme == "rabbitmq" && endpoint.Uri.Host == "topic")
                .ToList();

            senders.Select(endpoint => endpoint.Uri.ToString()).ShouldBe(
                ["rabbitmq://topic/orders.events/order.placed", "rabbitmq://topic/orders.events/order.status-changed"], ignoreOrder: true);
            senders.ShouldAllBe(endpoint => endpoint.Mode == EndpointMode.Durable);
        }
    }

    /// <summary>One transition per order: two history rows, one event log row, row version 1 and one OrderStatusChanged.v1 each.</summary>
    private async Task AssertProcessedOnce(List<Guid> ids, string? actor)
    {
        var changed = await _rabbit.WaitForMessagesAsync(_changedSpy, ids.Count, _timeout, Token);
        await Task.Delay(TimeSpan.FromSeconds(2), Token);
        changed = [.. changed, .. await _rabbit.GetMessagesAsync(_changedSpy, 20, Token)];
        changed.Select(message => message.Json.GetProperty("orderId").GetGuid()).Order().ShouldBe(ids.Order());
        await using var context = _postgres.NewContext();

        foreach (var id in ids)
        {
            var order = await context.Orders.Include(record => record.History).SingleAsync(record => record.Id == id, Token);
            order.History.Count.ShouldBe(2);
            order.RowVersion.ShouldBe(1);
            (await context.OrderEventLog.CountAsync(entry => entry.OrderId == id, Token)).ShouldBe(1);

            if (actor is not null)
            {
                order.History.OrderBy(history => history.Seq).Last().Actor.ShouldBe(actor);
            }
        }
    }

    private async Task InsertHoldRule()
    {
        await using var context = _postgres.NewContext();
        context.ConfirmationRules.Add(new ConfirmationRuleRecord
        {
            Id = Guid.NewGuid(),
            Version = 1,
            Name = "Hold every BRL order",
            Action = ConfirmationAction.Hold,
            Conditions = """[{ "kind": "TotalAbove", "amount": 1, "currency": "BRL" }]""",
            Priority = 1,
            CreatedBy = "admin-1",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Token);
    }

    private async Task<Guid> PlaceOrder(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent("""{"currency":"BRL","items":[{"sku":"KB-1","quantity":1}]}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, _shopper);
        request.Headers.Add(TestAuthHandler.NameHeader, "Ana Souza");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Token);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> Confirm(HttpClient client, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/orders/{id}/confirm")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, "admin-1");
        request.Headers.Add(TestAuthHandler.RolesHeader, "Admin");
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");

        return await client.SendAsync(request, Token);
    }

    private async Task<List<OrderStatus>> Statuses(List<Guid> ids)
    {
        await using var context = _postgres.NewContext();
        var statuses = await context.Orders.Where(record => ids.Contains(record.Id)).ToDictionaryAsync(record => record.Id, record => record.Status, Token);

        return ids.Select(id => statuses[id]).ToList();
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + _timeout;

        while (!await condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the condition did not hold in time");
            await Task.Delay(250, Token);
        }
    }

    private T Start<T>(T factory)
        where T : IAsyncDisposable
    {
        _hosts.Add(factory);
        _ = factory switch
        {
            WebApplicationFactory<Program> api => api.Services,
            WebApplicationFactory<OrdersWorker::Program> worker => worker.Services,
            _ => throw new ArgumentOutOfRangeException(nameof(factory)),
        };

        return factory;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class ApiFactory(string postgres, string rabbit, string catalogUrl) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Orders", postgres);
            builder.UseSetting("ConnectionStrings:RabbitMq", rabbit);
            builder.UseSetting("Orders:Persistence:AutoCreateMessageStorage", "true");
            builder.UseSetting("Catalog:BaseAddress", catalogUrl);
            builder.UseSetting("Catalog:Client:RetryDelay", "00:00:00.01");
            builder.ConfigureServices(services => services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
        }
    }

    private sealed class WorkerFactory(string postgres, string rabbit) : WebApplicationFactory<OrdersWorker::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Orders", postgres);
            builder.UseSetting("ConnectionStrings:RabbitMq", rabbit);
            builder.UseSetting("Orders:Persistence:AutoCreateMessageStorage", "true");
            builder.UseSetting("Orders:Worker:ScheduledJobPollingInterval", "00:00:00.1");
            builder.UseSetting("Orders:Confirmation:CacheDuration", "00:00:00.2");
        }
    }
}
