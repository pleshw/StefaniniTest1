using Microsoft.EntityFrameworkCore;
using Orders.Application.Ordering;
using Orders.Domain;
using Orders.Infrastructure.Persistence;

namespace Orders.IntegrationTests;

public sealed class OrderRepositoryTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset _now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public OrderRepositoryTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AddAsync_OrderWithItemsAndHistory_RoundTripsEveryField()
    {
        var order = NewOrder("ORD-RT-1");
        order.Hold("system:worker", "BRL above 5,000", _now.AddMinutes(1));
        await using var writer = _postgres.NewContext();
        await new OrderRepository(writer).AddAsync(order, Token);

        await using var reader = _postgres.NewContext();
        var stored = await new OrderRepository(reader).GetByIdAsync(order.Id, Token);

        stored.ShouldNotBeNull();
        stored.Number.ShouldBe("ORD-RT-1");
        stored.Client.ShouldBe(order.Client);
        stored.Channel.ShouldBe(OrderChannel.Admin);
        stored.Status.ShouldBe(OrderStatus.OnHold);
        stored.ItemCount.ShouldBe(5);
        stored.Subtotal.ShouldBe(order.Subtotal);
        stored.DiscountTotal.ShouldBe(order.DiscountTotal);
        stored.Total.ShouldBe(order.Total);
        stored.CreatedAt.ShouldBe(_now);
        stored.UpdatedAt.ShouldBe(_now.AddMinutes(1));
        stored.RowVersion.ShouldBe(1);
        stored.Items.ShouldBe(order.Items);
        stored.StatusHistory.ShouldBe(order.StatusHistory);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetByIdAsync_UnknownId_ReturnsNull()
    {
        await using var context = _postgres.NewContext();

        var stored = await new OrderRepository(context).GetByIdAsync(Guid.NewGuid(), Token);

        stored.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpdateAsync_NewTransition_StoresTheStatusAndTheNewHistoryRow()
    {
        var order = NewOrder("ORD-UP-1");
        await using (var writer = _postgres.NewContext())
        {
            await new OrderRepository(writer).AddAsync(order, Token);
        }

        order.Confirm("system:worker", _now.AddMinutes(2));
        await using (var updater = _postgres.NewContext())
        {
            await new OrderRepository(updater).UpdateAsync(order, 0, Token);
        }

        await using var reader = _postgres.NewContext();
        var stored = await new OrderRepository(reader).GetByIdAsync(order.Id, Token);
        stored.ShouldNotBeNull();
        stored.Status.ShouldBe(OrderStatus.Confirmed);
        stored.RowVersion.ShouldBe(1);
        stored.StatusHistory.Select(change => change.To).ShouldBe([OrderStatus.Pending, OrderStatus.Confirmed]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpdateAsync_RowVersionMovedMeanwhile_ThrowsConcurrencyExceptionAndKeepsTheFirstChange()
    {
        var order = NewOrder("ORD-CC-1");
        await using (var writer = _postgres.NewContext())
        {
            await new OrderRepository(writer).AddAsync(order, Token);
        }

        await using var firstContext = _postgres.NewContext();
        await using var secondContext = _postgres.NewContext();
        var first = (await new OrderRepository(firstContext).GetByIdAsync(order.Id, Token))!;
        var second = (await new OrderRepository(secondContext).GetByIdAsync(order.Id, Token))!;
        first.Confirm("admin-1", _now.AddMinutes(1));
        second.Block("admin-2", "fraud", _now.AddMinutes(1));
        await new OrderRepository(firstContext).UpdateAsync(first, 0, Token);

        await Should.ThrowAsync<ConcurrencyException>(() => new OrderRepository(secondContext).UpdateAsync(second, 0, Token));

        await using var reader = _postgres.NewContext();
        var stored = await new OrderRepository(reader).GetByIdAsync(order.Id, Token);
        stored!.Status.ShouldBe(OrderStatus.Confirmed);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SaveChangesAsync_StaleTokenOnTheRowItself_ThrowsDbUpdateConcurrencyException()
    {
        var order = NewOrder("ORD-CC-2");
        await using (var writer = _postgres.NewContext())
        {
            await new OrderRepository(writer).AddAsync(order, Token);
        }

        await using var stale = _postgres.NewContext();
        var record = await stale.Orders.SingleAsync(stored => stored.Id == order.Id, Token);
        await using (var other = _postgres.NewContext())
        {
            await other.Orders.Where(stored => stored.Id == order.Id).ExecuteUpdateAsync(set => set.SetProperty(stored => stored.RowVersion, 5), Token);
        }

        record.Status = OrderStatus.Confirmed;
        record.RowVersion = 1;

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(Token));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Migrations_Applied_CreateTheSearchColumnExtensionAndIndexes()
    {
        await using var context = _postgres.NewContext();

        var extensions = await context.Database
            .SqlQuery<string>($"SELECT extname AS \"Value\" FROM pg_extension")
            .ToListAsync(Token);
        var indexes = await context.Database
            .SqlQuery<string>($"SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename IN ('orders', 'idempotency_requests')")
            .ToListAsync(Token);

        extensions.ShouldContain("pg_trgm");
        indexes.ShouldContain("ix_orders_search");
        indexes.ShouldContain("ix_orders_client_trgm");
        indexes.ShouldContain("ix_orders_client_created");
        indexes.ShouldContain("ix_orders_status_created");
        indexes.ShouldContain("ix_idempotency_expires");
    }

    private static Order NewOrder(string number) => Order.Place(
        Guid.NewGuid(),
        number,
        ClientRef.Create(Guid.NewGuid(), "Ana Souza"),
        "admin-1",
        OrderChannel.Admin,
        [
            OrderItem.Create("SKU-1", "Keyboard", Money.Create(19.99m, Currency.Create("BRL")), 3, 3, Money.Create(5m, Currency.Create("BRL"))),
            OrderItem.Create("SKU-2", "Mouse", Money.Create(10m, Currency.Create("BRL")), 1, 2),
        ],
        _now);

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
