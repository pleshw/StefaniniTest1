using Microsoft.EntityFrameworkCore;
using Orders.Application.Events;
using Orders.Application.Ordering;
using Orders.Application.Retention;
using Orders.Domain;
using Orders.Infrastructure.Persistence;

namespace Orders.IntegrationTests;

/// <summary>The event log query and the retention deletes on PostgreSQL (Testcontainers) with the real migrations and indexes.</summary>
public sealed class EventLogAndRetentionPostgresTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset _now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync()
    {
        // The two tables are shared by the tests of this class, so each one starts from nothing.
        await using var context = postgres.NewContext();
        await context.Database.ExecuteSqlAsync($"DELETE FROM idempotency_requests", Token);
        await context.Database.ExecuteSqlAsync($"DELETE FROM order_event_log", Token);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task List_PagesByTimeThenIdWithoutSkippingOrRepeatingEventsAtTheSameTime()
    {
        var order = await InsertOrder();
        await InsertEvent(order, "OrderStatusChanged.v1", _now.AddMinutes(2));
        await InsertEvent(order, "OrderStatusChanged.v1", _now.AddMinutes(1));
        for (var index = 0; index < 4; index++)
        {
            await InsertEvent(order, "DeadLetterReplayed.v1", _now);
        }

        var seen = new List<OrderEventDto>();
        EventCursor? after = null;

        while (true)
        {
            await using var context = postgres.NewContext();
            var page = await new OrderEventLogQueries(context).ListAsync(new EventFilter(null, null, after, 3), Token);
            seen.AddRange(page.Take(2));

            if (page.Count <= 2)
            {
                break;
            }

            after = new EventCursor(page[1].OccurredAt, page[1].Id);
        }

        seen.Count.ShouldBe(6);
        seen.Select(item => item.Id).Distinct().Count().ShouldBe(6);
        seen.Select(item => (item.OccurredAt, item.Id)).ShouldBe(seen.OrderBy(item => item.OccurredAt).ThenBy(item => item.Id).Select(item => (item.OccurredAt, item.Id)).ToList());
        seen[^1].OccurredAt.ShouldBe(_now.AddMinutes(2));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task List_TypeAndOrderFilters_NarrowTheResult()
    {
        var first = await InsertOrder();
        var second = await InsertOrder();
        await InsertEvent(first, "OrderStatusChanged.v1", _now);
        await InsertEvent(first, "DeadLetterReplayed.v1", _now.AddMinutes(1));
        await InsertEvent(second, "OrderStatusChanged.v1", _now.AddMinutes(2));
        await using var context = postgres.NewContext();
        var queries = new OrderEventLogQueries(context);

        (await queries.ListAsync(new EventFilter(first, null, null, 10), Token)).Count.ShouldBe(2);
        (await queries.ListAsync(new EventFilter(null, "OrderStatusChanged.v1", null, 10), Token)).Count.ShouldBe(2);
        var both = (await queries.ListAsync(new EventFilter(second, "OrderStatusChanged.v1", null, 10), Token)).ShouldHaveSingleItem();
        both.Payload.GetProperty("note").GetString().ShouldBe("OrderStatusChanged.v1");
        (await queries.ListAsync(new EventFilter(second, "DeadLetterReplayed.v1", null, 10), Token)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Retention_DeletesOnlyExpiredIdempotencyRecordsAndOldEvents()
    {
        var order = await InsertOrder();
        var expired = await InsertIdempotency(_now.AddSeconds(-1));
        var atTheLimit = await InsertIdempotency(_now);
        var valid = await InsertIdempotency(_now.AddHours(23));
        var old = await InsertEvent(order, "OrderStatusChanged.v1", _now.AddDays(-30).AddSeconds(-1));
        var exactlyThirtyDays = await InsertEvent(order, "OrderStatusChanged.v1", _now.AddDays(-30));
        var recent = await InsertEvent(order, "OrderStatusChanged.v1", _now.AddDays(-1));

        var result = await Runner().RunAsync(Token);

        result.ShouldBe(new RetentionResult(1, 1));
        await using var context = postgres.NewContext();
        (await context.IdempotencyRequests.Select(record => record.IdempotencyKey).ToListAsync(Token)).Order().ShouldBe(new[] { atTheLimit, valid }.Order().ToList());
        expired.ShouldNotBe(Guid.Empty);
        (await context.OrderEventLog.Select(entry => entry.Id).ToListAsync(Token)).Order().ShouldBe(new[] { exactlyThirtyDays, recent }.Order().ToList());
        old.ShouldBeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Retention_ALargeBacklog_IsDeletedInBatchesOfOneThousand()
    {
        var order = await InsertOrder();
        await using (var context = postgres.NewContext())
        {
            var expired = _now.AddDays(-2);
            var old = _now.AddDays(-40);
            await context.Database.ExecuteSqlAsync(
                $$"""
                INSERT INTO idempotency_requests (client_id, idempotency_key, request_hash, response_status, response_body, order_id, created_at, expires_at)
                SELECT gen_random_uuid(), gen_random_uuid(), '\x00'::bytea, 201, '{}'::jsonb, {{order}}, {{expired}}, {{expired}} FROM generate_series(1, 2500)
                """,
                Token);
            await context.Database.ExecuteSqlAsync(
                $$"""
                INSERT INTO order_event_log (order_id, type, payload, occurred_at)
                SELECT {{order}}, 'OrderStatusChanged.v1', '{}'::jsonb, {{old}} FROM generate_series(1, 2500)
                """,
                Token);
        }

        var calls = new List<int>();
        var result = await new RetentionRunner(new CountingStore(calls, postgres), new FixedTimeProvider(_now), new RetentionOptions()).RunAsync(Token);

        result.ShouldBe(new RetentionResult(2500, 2500));
        calls.ShouldBe([1000, 1000, 500, 1000, 1000, 500]);
        await using var read = postgres.NewContext();
        (await read.IdempotencyRequests.CountAsync(Token)).ShouldBe(0);
        (await read.OrderEventLog.CountAsync(Token)).ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Retention_NothingExpired_DeletesNothing()
    {
        var order = await InsertOrder();
        await InsertIdempotency(_now.AddHours(1));
        await InsertEvent(order, "OrderStatusChanged.v1", _now);

        (await Runner().RunAsync(Token)).ShouldBe(new RetentionResult(0, 0));
    }

    private RetentionRunner Runner()
    {
        var context = postgres.NewContext();

        return new RetentionRunner(new RetentionStore(context), new FixedTimeProvider(_now), new RetentionOptions());
    }

    private async Task<Guid> InsertOrder()
    {
        var id = Guid.NewGuid();
        var order = Order.Place(
            id, OrderNumbers.For(id, _now), ClientRef.Create(Guid.NewGuid(), "Ana"), "shopper-1", OrderChannel.Shop,
            [OrderItem.Create("KB-1", "Keyboard", Money.Create(10m, Currency.Create("BRL")), 1, 1)], _now);
        await using var context = postgres.NewContext();
        context.Orders.Add(order.ToRecord());
        await context.SaveChangesAsync(Token);

        return id;
    }

    private async Task<long> InsertEvent(Guid orderId, string type, DateTimeOffset occurredAt)
    {
        await using var context = postgres.NewContext();
        var record = new OrderEventLogRecord { OrderId = orderId, Type = type, Payload = $$"""{ "note": "{{type}}" }""", OccurredAt = occurredAt };
        context.OrderEventLog.Add(record);
        await context.SaveChangesAsync(Token);

        return record.Id;
    }

    private async Task<Guid> InsertIdempotency(DateTimeOffset expiresAt)
    {
        var key = Guid.NewGuid();
        await using var context = postgres.NewContext();
        context.IdempotencyRequests.Add(new IdempotencyRequestRecord
        {
            ClientId = Guid.NewGuid(),
            IdempotencyKey = key,
            RequestHash = [1],
            ResponseStatus = 201,
            ResponseBody = "{}",
            OrderId = Guid.NewGuid(),
            CreatedAt = expiresAt.AddHours(-24),
            ExpiresAt = expiresAt,
        });
        await context.SaveChangesAsync(Token);

        return key;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The real store, recording how many rows each batch deleted.</summary>
    private sealed class CountingStore(List<int> calls, PostgresFixture postgres) : IRetentionStore
    {
        public async Task<int> DeleteExpiredIdempotencyAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
        {
            await using var context = postgres.NewContext();
            var deleted = await new RetentionStore(context).DeleteExpiredIdempotencyAsync(now, batchSize, cancellationToken);
            calls.Add(deleted);

            return deleted;
        }

        public async Task<int> DeleteEventsOlderThanAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
        {
            await using var context = postgres.NewContext();
            var deleted = await new RetentionStore(context).DeleteEventsOlderThanAsync(cutoff, batchSize, cancellationToken);
            calls.Add(deleted);

            return deleted;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
