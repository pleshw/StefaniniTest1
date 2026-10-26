using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Orders.Application.Ordering;
using Orders.Domain;
using Orders.Infrastructure.Persistence;

namespace Orders.IntegrationTests;

/// <summary>The order queries against PostgreSQL 17 (Testcontainers) with the real migrations, indexes and generated search vector.</summary>
public sealed class OrderQueryPostgresTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset _start = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly Guid _ana = Guid.NewGuid();
    private readonly Guid _bruno = Guid.NewGuid();
    private readonly List<Order> _seeded = [];

    public async ValueTask InitializeAsync()
    {
        _seeded.Add(await Seed(_ana, "Ana Souza", _start, OrderStatus.Pending));
        _seeded.Add(await Seed(_ana, "Ana Souza", _start.AddHours(1), OrderStatus.OnHold));
        _seeded.Add(await Seed(_bruno, "Bruno Lima", _start.AddHours(2), OrderStatus.Pending));
        _seeded.Add(await Seed(_bruno, "Bruno Lima", _start.AddHours(2), OrderStatus.Confirmed));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListAsync_ClientFilter_ReturnsOnlyThatClientNewestFirst()
    {
        var found = await List(new OrderListFilter(_ana, null, null, null, null, null, 10));

        found.Select(order => order.Id).ShouldBe([_seeded[1].Id, _seeded[0].Id]);
        found.ShouldAllBe(order => order.Client.Id == _ana && order.Client.Name == "Ana Souza");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListAsync_StatusFilter_ReturnsOnlyThatStatus()
    {
        var found = await List(new OrderListFilter(_ana, OrderStatus.OnHold, null, null, null, null, 10));

        found.ShouldHaveSingleItem().Id.ShouldBe(_seeded[1].Id);
        found[0].Status.ShouldBe("OnHold");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListAsync_FromAndTo_AreInclusiveAndExclusive()
    {
        var found = await List(new OrderListFilter(_ana, null, _start, _start.AddHours(1), null, null, 10));

        found.ShouldHaveSingleItem().Id.ShouldBe(_seeded[0].Id);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListAsync_SearchByOrderNumber_FindsTheOrder()
    {
        var found = await List(new OrderListFilter(null, null, null, null, _seeded[2].Number, null, 10));

        found.ShouldHaveSingleItem().Id.ShouldBe(_seeded[2].Id);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListAsync_SearchByClientNameWord_FindsTheClientsOrders()
    {
        var found = await List(new OrderListFilter(_bruno, null, null, null, "lima", null, 10));

        found.Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListAsync_SearchByPartOfAClientName_FindsThemThroughTheTrigramIndex()
    {
        var found = await List(new OrderListFilter(_ana, null, null, null, "ouz", null, 10));

        found.Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListAsync_CursorAfterEachPage_WalksEveryOrderOnceIncludingTies()
    {
        var walked = new List<Guid>();
        OrderCursor? after = null;

        do
        {
            var page = await List(new OrderListFilter(_bruno, null, null, null, null, after, 1));
            walked.AddRange(page.Select(order => order.Id));
            after = page.Count == 0 ? null : new OrderCursor(page[^1].CreatedAt, page[^1].Id);
        }
        while (after is not null && walked.Count < 10);

        walked.Count.ShouldBe(2);
        walked.ShouldBe([_seeded[2].Id, _seeded[3].Id], ignoreOrder: true);
        walked.Distinct().Count().ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetAsync_OwnerOrAdmin_ReturnsTheOrderWithItemsInLineOrder()
    {
        await using var context = postgres.NewContext();
        var queries = new OrderQueries(context);

        var own = await queries.GetAsync(_seeded[0].Id, _ana, Token);
        var admin = await queries.GetAsync(_seeded[0].Id, null, Token);

        own.ShouldNotBeNull();
        own.Items.Select(item => item.Sku).ShouldBe(["KB-1", "MS-1"]);
        own.Total.ShouldBe(_seeded[0].Total.Amount);
        admin.ShouldBe(own, new OrderDtoComparer());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetAsync_AnotherClientsOrder_IsMissing()
    {
        await using var context = postgres.NewContext();

        (await new OrderQueries(context).GetAsync(_seeded[0].Id, _bruno, Token)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetHistoryAsync_OwnOrder_ReturnsTheEntriesInSequence()
    {
        await using var context = postgres.NewContext();

        var history = await new OrderQueries(context).GetHistoryAsync(_seeded[1].Id, _ana, Token);

        history.ShouldNotBeNull();
        history.Select(change => change.To).ShouldBe(["Pending", "OnHold"]);
        history[0].From.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetHistoryAsync_AnotherClientsOrder_IsMissing()
    {
        await using var context = postgres.NewContext();

        (await new OrderQueries(context).GetHistoryAsync(_seeded[1].Id, _bruno, Token)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListQuery_ShopperList_CanUseTheClientIndex()
    {
        await using var context = postgres.NewContext();
        await context.Database.OpenConnectionAsync(Token);
        await using var transaction = await context.Database.BeginTransactionAsync(Token);
        await context.Database.ExecuteSqlAsync($"SET LOCAL enable_seqscan = off", Token);
        var filter = new OrderListFilter(_ana, null, null, null, null, new OrderCursor(_start.AddDays(1), Guid.Empty), 21);

        // The command EF Core runs, with its parameters, only prefixed with EXPLAIN.
        await using var command = new OrderQueries(context).ListQuery(filter).CreateDbCommand();
        command.CommandText = "EXPLAIN " + command.CommandText;
        command.Transaction = transaction.GetDbTransaction();
        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token))
            {
                plan.Add(reader.GetString(0));
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, plan));
        string.Join(' ', plan).ShouldContain("ix_orders_client_created");
    }

    private async Task<IReadOnlyList<OrderSummaryDto>> List(OrderListFilter filter)
    {
        await using var context = postgres.NewContext();

        return await new OrderQueries(context).ListAsync(filter, Token);
    }

    private async Task<Order> Seed(Guid clientId, string clientName, DateTimeOffset createdAt, OrderStatus status)
    {
        var brl = Currency.Create("BRL");
        var id = Guid.NewGuid();
        var order = Order.Place(
            id,
            OrderNumbers.For(id, createdAt),
            ClientRef.Create(clientId, clientName),
            clientId.ToString(),
            OrderChannel.Shop,
            [OrderItem.Create("KB-1", "Keyboard", Money.Create(19.99m, brl), 3, 1), OrderItem.Create("MS-1", "Mouse", Money.Create(10m, brl), 1, 2)],
            createdAt);

        switch (status)
        {
            case OrderStatus.OnHold:
                order.Hold("system:worker", "rule", createdAt);
                break;
            case OrderStatus.Confirmed:
                order.Confirm("system:worker", createdAt);
                break;
        }

        await using var context = postgres.NewContext();
        context.Orders.Add(order.ToRecord());
        await context.SaveChangesAsync(Token);

        return order;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class OrderDtoComparer : IEqualityComparer<OrderDto>
    {
        public bool Equals(OrderDto? x, OrderDto? y) =>
            x is not null && y is not null && x.Id == y.Id && x.Total == y.Total && x.Items.SequenceEqual(y.Items);

        public int GetHashCode(OrderDto obj) => obj.Id.GetHashCode();
    }
}
