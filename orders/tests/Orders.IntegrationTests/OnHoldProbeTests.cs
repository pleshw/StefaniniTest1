using Microsoft.EntityFrameworkCore;
using Orders.Application.Ordering;
using Orders.Domain;
using Orders.Infrastructure.Persistence;

namespace Orders.IntegrationTests;

/// <summary>The OnHold reading behind the <c>orders.on_hold</c> gauges, on PostgreSQL (Testcontainers) with the real migrations.</summary>
public sealed class OnHoldProbeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset _now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync()
    {
        await using var context = postgres.NewContext();
        await context.Database.ExecuteSqlAsync($"TRUNCATE orders CASCADE", Token);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadAsync_NoOrders_ReturnsZeroAndNoAge()
    {
        var snapshot = await Read();

        snapshot.Count.ShouldBe(0);
        snapshot.OldestSince.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadAsync_OtherStatusesOnly_ReturnsZeroAndNoAge()
    {
        await Insert(OrderStatus.Confirmed, _now.AddHours(-9));
        await Insert(OrderStatus.Pending, _now.AddHours(-8));

        var snapshot = await Read();

        snapshot.Count.ShouldBe(0);
        snapshot.OldestSince.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadAsync_SomeOrdersOnHold_CountsThemAndReturnsWhenTheOldestWasHeld()
    {
        await Insert(OrderStatus.OnHold, _now.AddHours(-1));
        await Insert(OrderStatus.OnHold, _now.AddHours(-6));
        await Insert(OrderStatus.OnHold, _now.AddMinutes(-5));
        await Insert(OrderStatus.Confirmed, _now.AddHours(-20));

        var snapshot = await Read();

        snapshot.Count.ShouldBe(3);
        snapshot.OldestSince.ShouldBe(_now.AddHours(-6));
    }

    private async Task<Orders.Application.Observability.OnHoldSnapshot> Read()
    {
        await using var context = postgres.NewContext();

        return await new OnHoldProbe(context).ReadAsync(Token);
    }

    /// <summary>An order that has been in <paramref name="status"/> since <paramref name="since"/> (updated_at is when the status last changed).</summary>
    private async Task Insert(OrderStatus status, DateTimeOffset since)
    {
        var id = Guid.NewGuid();
        var order = Order.Place(
            id, OrderNumbers.For(id, _now), ClientRef.Create(Guid.NewGuid(), "Ana"), "shopper-1", OrderChannel.Shop,
            [OrderItem.Create("KB-1", "Keyboard", Money.Create(10m, Currency.Create("BRL")), 1, 1)], _now.AddDays(-2));
        var record = order.ToRecord();
        record.Status = status;
        record.UpdatedAt = since;
        await using var context = postgres.NewContext();
        context.Orders.Add(record);
        await context.SaveChangesAsync(Token);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
