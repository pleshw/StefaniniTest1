extern alias OrdersWorker;

using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orders.Application.Ordering;
using Orders.Contracts;
using Orders.Domain;
using Orders.Domain.Confirmation;
using Orders.Infrastructure.Persistence;

namespace Orders.IntegrationTests;

/// <summary>
/// The whole automatic confirmation on PostgreSQL and RabbitMQ (Testcontainers): an OrderPlaced.v1 message reaches the
/// real Orders.Worker, which holds or confirms the order, writes the history and the event log, and publishes OrderStatusChanged.v1.
/// </summary>
public sealed class AutomaticConfirmationRabbitMqTests : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly Currency _brl = Currency.Create("BRL");

    private readonly PostgresFixture _postgres;
    private readonly RabbitMqFixture _rabbit;
    private readonly string _spyQueue = $"test.status-changed.{Guid.NewGuid():N}";
    private WebApplicationFactory<OrdersWorker::Program>? _factory;

    public AutomaticConfirmationRabbitMqTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    {
        _postgres = postgres;
        _rabbit = rabbit;
    }

    public async ValueTask InitializeAsync()
    {
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

        _factory = new WorkerFactory(_postgres.ConnectionString, _rabbit.AmqpUri);
        _ = _factory.Services;
        await _rabbit.BindQueueAsync(_spyQueue, "orders.events", "order.status-changed", Token);
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
    public async Task OrderPlaced_LargeBrlOrderWithTheSeedRule_IsHeldAndTheChangeIsLoggedAndPublished()
    {
        await InsertRule("Large BRL order", """[{ "kind": "TotalAbove", "amount": 5000, "currency": "BRL" }]""");
        var order = await InsertOrder(6000m);

        await Announce(order);

        await WaitForStatus(order.Id, OrderStatus.OnHold);
        var stored = await Read(order.Id);
        stored.Status.ShouldBe(OrderStatus.OnHold);
        stored.RowVersion.ShouldBe(1);
        var change = stored.StatusHistory[^1];
        (change.From, change.To, change.Reason, change.Actor).ShouldBe((OrderStatus.Pending, OrderStatus.OnHold, "Large BRL order", "system:worker"));
        var log = await EventLog(order.Id);
        log.ShouldHaveSingleItem().Type.ShouldBe("OrderStatusChanged.v1");
        var published = await _rabbit.WaitForMessagesAsync(_spyQueue, 1, TimeSpan.FromSeconds(30), Token);
        published.Count.ShouldBe(1);
        published[0].RoutingKey.ShouldBe("order.status-changed");
        var body = JsonSerializer.Deserialize<OrderStatusChangedV1>(published[0].Payload, _json)!;
        (body.OrderId, body.From, body.To, body.Reason, body.Actor).ShouldBe((order.Id, "Pending", "OnHold", "Large BRL order", "system:worker"));
        JsonSerializer.Deserialize<OrderStatusChangedV1>(log[0].Payload, _json).ShouldBe(body);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OrderPlaced_NoRuleMatches_ConfirmsTheOrderAsTheWorker()
    {
        await InsertRule("Large BRL order", """[{ "kind": "TotalAbove", "amount": 5000, "currency": "BRL" }]""");
        var order = await InsertOrder(100m);

        await Announce(order);

        await WaitForStatus(order.Id, OrderStatus.Confirmed);
        var change = (await Read(order.Id)).StatusHistory[^1];
        (change.From, change.To, change.Reason, change.Actor).ShouldBe((OrderStatus.Pending, OrderStatus.Confirmed, null, "system:worker"));
        (await EventLog(order.Id)).Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OrderPlaced_AdminActedFirst_LeavesTheOrderAsTheAdminLeftIt()
    {
        await InsertRule("Large BRL order", """[{ "kind": "TotalAbove", "amount": 5000, "currency": "BRL" }]""");
        var order = await InsertOrder(6000m);
        await Block(order.Id);

        await Announce(order);
        await Task.Delay(TimeSpan.FromSeconds(3), Token);

        var stored = await Read(order.Id);
        stored.Status.ShouldBe(OrderStatus.Blocked);
        stored.StatusHistory.Count.ShouldBe(2);
        (await EventLog(order.Id)).ShouldBeEmpty();
        (await _rabbit.GetMessagesAsync(_spyQueue, 5, Token)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OrderPlaced_AnnouncedAgainWithAnotherMessageId_DoesNotTransitionTwice()
    {
        await InsertRule("Large BRL order", """[{ "kind": "TotalAbove", "amount": 5000, "currency": "BRL" }]""");
        var order = await InsertOrder(6000m);
        await Announce(order);
        await WaitForStatus(order.Id, OrderStatus.OnHold);

        await Announce(order);
        await Task.Delay(TimeSpan.FromSeconds(3), Token);

        var stored = await Read(order.Id);
        stored.Status.ShouldBe(OrderStatus.OnHold);
        stored.StatusHistory.Count.ShouldBe(2);
        (await EventLog(order.Id)).Count.ShouldBe(1);
        (await _rabbit.GetMessagesAsync(_spyQueue, 5, Token)).Count.ShouldBe(1);
    }

    private async Task InsertRule(string name, string conditions)
    {
        await using var context = _postgres.NewContext();
        context.ConfirmationRules.Add(new ConfirmationRuleRecord
        {
            Id = Guid.NewGuid(),
            Version = 1,
            Name = name,
            Action = ConfirmationAction.Hold,
            Conditions = conditions,
            Priority = 1,
            CreatedBy = "admin-1",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Token);
    }

    private async Task<Order> InsertOrder(decimal unitPrice)
    {
        var id = Guid.NewGuid();
        var placed = DateTimeOffset.UtcNow;
        var order = Order.Place(
            id, OrderNumbers.For(id, placed), ClientRef.Create(Guid.NewGuid(), "Ana"), "shopper-1", OrderChannel.Shop,
            [OrderItem.Create("KB-1", "Keyboard", Money.Create(unitPrice, _brl), 1, 1)], placed);
        await using var context = _postgres.NewContext();
        context.Orders.Add(order.ToRecord());
        await context.SaveChangesAsync(Token);

        return order;
    }

    private async Task Block(Guid id)
    {
        await using var context = _postgres.NewContext();
        var repository = new OrderRepository(context);
        var order = (await repository.GetByIdAsync(id, Token))!;
        order.Block("admin-1", "fraud suspected", DateTimeOffset.UtcNow);
        await repository.UpdateAsync(order, 0, Token);
    }

    private async Task<Order> Read(Guid id)
    {
        await using var context = _postgres.NewContext();

        return (await new OrderRepository(context).GetByIdAsync(id, Token))!;
    }

    private async Task<List<OrderEventLogRecord>> EventLog(Guid orderId)
    {
        await using var context = _postgres.NewContext();

        return await context.OrderEventLog.Where(log => log.OrderId == orderId).OrderBy(log => log.Id).ToListAsync(Token);
    }

    private async Task WaitForStatus(Guid id, OrderStatus status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline && (await Read(id)).Status != status)
        {
            await Task.Delay(200, Token);
        }
    }

    private Task Announce(Order order) => _rabbit.PublishAsync(
        "orders.events",
        "order.placed",
        new
        {
            type = "Orders.Contracts.OrderPlacedV1",
            message_id = Guid.NewGuid().ToString(),
            content_type = "application/json",
            headers = new Dictionary<string, string> { ["occurred-at"] = order.CreatedAt.ToString("O") },
        },
        JsonSerializer.Serialize(
            new OrderPlacedV1(order.Id, order.Number, order.Client.Id, "shop", "BRL", order.Total.Amount, order.ItemCount, order.CreatedAt), _json),
        Token);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class WorkerFactory(string postgres, string rabbit) : WebApplicationFactory<OrdersWorker::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Orders", postgres);
            builder.UseSetting("ConnectionStrings:RabbitMq", rabbit);
            builder.UseSetting("Orders:Persistence:AutoCreateMessageStorage", "true");
            builder.UseSetting("Orders:Worker:ScheduledJobPollingInterval", "00:00:00.1");
        }
    }
}
