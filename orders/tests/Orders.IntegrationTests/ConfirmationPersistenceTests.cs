using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orders.Application.Ordering;
using Orders.Domain;
using Orders.Domain.Confirmation;
using Orders.Infrastructure.Persistence;

namespace Orders.IntegrationTests;

/// <summary>Confirmation rules, the event log, the earlier-orders query and the seed on PostgreSQL (Testcontainers) with the real migrations.</summary>
public sealed class ConfirmationPersistenceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string _large = """[{ "kind": "TotalAbove", "amount": 5000, "currency": "BRL" }]""";

    private static readonly DateTimeOffset _now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetActiveAsync_NewVersion_SupersedesTheOldOne()
    {
        var id = Guid.NewGuid();
        await Insert(Rule(id, 1, "old"));
        await Insert(Rule(id, 2, "new"));

        var rule = (await Active()).Single(found => found.Id == id);

        rule.Version.ShouldBe(2);
        rule.Name.ShouldBe("new");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetActiveAsync_InactiveAndDeactivatedRules_AreIgnored()
    {
        var inactive = Guid.NewGuid();
        var deactivated = Guid.NewGuid();
        await Insert(Rule(inactive, 1, "never active", isActive: false));
        await Insert(Rule(deactivated, 1, "was active"));
        await Insert(Rule(deactivated, 2, "was active", isActive: false));

        var ids = (await Active()).Select(rule => rule.Id).ToList();

        ids.ShouldNotContain(inactive);
        ids.ShouldNotContain(deactivated);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetActiveAsync_OrdersByPriorityThenId()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
        var second = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
        var low = Guid.Parse("00000000-0000-0000-0000-0000000000a9");
        await Insert(Rule(second, 1, "second", priority: -5));
        await Insert(Rule(first, 1, "first", priority: -5));
        await Insert(Rule(low, 1, "lowest priority number", priority: -9));

        var ids = (await Active()).Select(rule => rule.Id).Where(id => id == first || id == second || id == low).ToList();

        ids.ShouldBe([low, first, second]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConfirmationRules_ActionOtherThanHold_IsRejectedByTheCheckConstraint()
    {
        await using var context = postgres.NewContext();

        await Should.ThrowAsync<Exception>(() => context.Database.ExecuteSqlAsync(
            $"INSERT INTO confirmation_rules (id, version, name, action, conditions, priority, is_active, created_by, created_at) VALUES ({Guid.NewGuid()}, 1, 'x', 'Block', '[]'::jsonb, 1, true, 'admin', now())",
            Token));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EventLog_Append_StoresTheContractPayloadOfTheEvent()
    {
        var order = await InsertOrder(Guid.NewGuid(), _now);
        await using var context = postgres.NewContext();

        await new OrderEventLog(context).AppendAsync(
            new OrderStatusChanged(order, OrderStatus.Pending, OrderStatus.OnHold, "Large BRL order", "system:worker", _now.AddMinutes(1)), Token);

        await using var read = postgres.NewContext();
        var row = await read.OrderEventLog.SingleAsync(log => log.OrderId == order, Token);
        row.Type.ShouldBe("OrderStatusChanged.v1");
        row.OccurredAt.ShouldBe(_now.AddMinutes(1));
        row.Id.ShouldBeGreaterThan(0);
        using var payload = JsonDocument.Parse(row.Payload);
        payload.RootElement.GetProperty("orderId").GetGuid().ShouldBe(order);
        payload.RootElement.GetProperty("from").GetString().ShouldBe("Pending");
        payload.RootElement.GetProperty("to").GetString().ShouldBe("OnHold");
        payload.RootElement.GetProperty("reason").GetString().ShouldBe("Large BRL order");
        payload.RootElement.GetProperty("actor").GetString().ShouldBe("system:worker");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EventLog_ForAnUnknownOrder_IsRejectedByTheForeignKey()
    {
        await using var context = postgres.NewContext();

        await Should.ThrowAsync<DbUpdateException>(() => new OrderEventLog(context).AppendAsync(
            new OrderStatusChanged(Guid.NewGuid(), OrderStatus.Pending, OrderStatus.Confirmed, null, "system:worker", _now), Token));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ClientHasEarlierOrdersAsync_LooksOnlyAtOtherOrdersPlacedBefore()
    {
        var client = Guid.NewGuid();
        var first = await InsertOrder(client, _now);
        var second = await InsertOrder(client, _now.AddMinutes(5));
        var other = Guid.NewGuid();
        var someoneElse = await InsertOrder(other, _now.AddMinutes(-5));
        await using var context = postgres.NewContext();
        var queries = new OrderQueries(context);

        (await queries.ClientHasEarlierOrdersAsync(client, first, _now, Token)).ShouldBeFalse();
        (await queries.ClientHasEarlierOrdersAsync(client, second, _now.AddMinutes(5), Token)).ShouldBeTrue();
        (await queries.ClientHasEarlierOrdersAsync(other, someoneElse, _now.AddMinutes(-5), Token)).ShouldBeFalse();
        (await queries.ClientHasEarlierOrdersAsync(client, Guid.NewGuid(), _now.AddMinutes(10), Token)).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ClientHasEarlierOrdersAsync_SamePlacementTime_TheLowerIdCountsAsEarlier()
    {
        var client = Guid.NewGuid();
        var a = await InsertOrder(client, _now, Guid.Parse("a0000000-0000-0000-0000-000000000000"));
        var b = await InsertOrder(client, _now, Guid.Parse("b0000000-0000-0000-0000-000000000000"));
        await using var context = postgres.NewContext();
        var queries = new OrderQueries(context);

        (await queries.ClientHasEarlierOrdersAsync(client, a, _now, Token)).ShouldBeFalse();
        (await queries.ClientHasEarlierOrdersAsync(client, b, _now, Token)).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Seeder_AddsTheLargeOrderRuleOnceAndTheWorkerAcceptsIt()
    {
        await using (var context = postgres.NewContext())
        {
            await ConfirmationRuleSeeder.SeedAsync(context, TimeProvider.System, Token);
        }

        await using (var context = postgres.NewContext())
        {
            await ConfirmationRuleSeeder.SeedAsync(context, TimeProvider.System, Token);
        }

        await using var read = postgres.NewContext();
        (await read.ConfirmationRules.CountAsync(rule => rule.Id == ConfirmationRuleSeeder.LargeOrderRuleId, Token)).ShouldBe(1);
        var seeded = (await new ConfirmationRuleRepository(read).GetActiveAsync(Token)).Single(rule => rule.Id == ConfirmationRuleSeeder.LargeOrderRuleId);
        seeded.Name.ShouldBe("Large BRL order");
        seeded.Action.ShouldBe(ConfirmationAction.Hold);
    }

    private async Task<IReadOnlyList<ConfirmationRule>> Active()
    {
        await using var context = postgres.NewContext();

        return await new ConfirmationRuleRepository(context).GetActiveAsync(Token);
    }

    private async Task Insert(ConfirmationRuleRecord record)
    {
        await using var context = postgres.NewContext();
        context.ConfirmationRules.Add(record);
        await context.SaveChangesAsync(Token);
    }

    private async Task<Guid> InsertOrder(Guid clientId, DateTimeOffset placedAt, Guid? id = null)
    {
        var orderId = id ?? Guid.NewGuid();
        var order = Order.Place(
            orderId,
            OrderNumbers.For(orderId, placedAt),
            ClientRef.Create(clientId, "Ana"),
            clientId.ToString(),
            OrderChannel.Shop,
            [OrderItem.Create("KB-1", "Keyboard", Money.Create(10m, Currency.Create("BRL")), 1, 1)],
            placedAt);
        await using var context = postgres.NewContext();
        context.Orders.Add(order.ToRecord());
        await context.SaveChangesAsync(Token);

        return orderId;
    }

    private static ConfirmationRuleRecord Rule(Guid id, int version, string name, bool isActive = true, int priority = 10) => new()
    {
        Id = id,
        Version = version,
        Name = name,
        Action = ConfirmationAction.Hold,
        Conditions = _large,
        Priority = priority,
        IsActive = isActive,
        CreatedBy = "admin-1",
        CreatedAt = _now,
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
