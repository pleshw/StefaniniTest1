using Microsoft.EntityFrameworkCore;
using Orders.Application.Idempotency;
using Orders.Infrastructure.Persistence;

namespace Orders.IntegrationTests;

public sealed class IdempotencyStoreTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset _now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public IdempotencyStoreTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TryReserveAsync_NewKey_ReturnsTrueAndExpiresInTwentyFourHours()
    {
        var client = Guid.NewGuid();
        var key = Guid.NewGuid();
        await using var context = _postgres.NewContext();

        var reserved = await Store(context).TryReserveAsync(client, key, [1, 2, 3], Guid.NewGuid(), Token);

        reserved.ShouldBeTrue();
        var row = await context.IdempotencyRequests.AsNoTracking().SingleAsync(request => request.ClientId == client, Token);
        row.CreatedAt.ShouldBe(_now);
        row.ExpiresAt.ShouldBe(_now.AddHours(24));
        row.RequestHash.ShouldBe([1, 2, 3]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TryReserveAsync_SameKeyTwice_ReturnsFalseTheSecondTimeAndKeepsTheFirstRow()
    {
        var client = Guid.NewGuid();
        var key = Guid.NewGuid();
        var firstOrder = Guid.NewGuid();
        await using var context = _postgres.NewContext();
        await Store(context).TryReserveAsync(client, key, [1], firstOrder, Token);

        var second = await Store(context).TryReserveAsync(client, key, [2], Guid.NewGuid(), Token);

        second.ShouldBeFalse();
        var stored = await Store(context).TryGetAsync(client, key, Token);
        stored!.OrderId.ShouldBe(firstOrder);
        stored.RequestHash.ShouldBe([1]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TryReserveAsync_SameKeyForAnotherClient_IsIndependent()
    {
        var key = Guid.NewGuid();
        await using var context = _postgres.NewContext();
        await Store(context).TryReserveAsync(Guid.NewGuid(), key, [1], Guid.NewGuid(), Token);

        var other = await Store(context).TryReserveAsync(Guid.NewGuid(), key, [1], Guid.NewGuid(), Token);

        other.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TryReserveAsync_TenConcurrentRequestsWithTheSameKey_ReservesExactlyOnce()
    {
        var client = Guid.NewGuid();
        var key = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var context = _postgres.NewContext();
            return await Store(context).TryReserveAsync(client, key, [1], Guid.NewGuid(), Token);
        }));

        results.Count(reserved => reserved).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CompleteAsync_ReservedKey_StoresTheStatusAndBodyForReplay()
    {
        var client = Guid.NewGuid();
        var key = Guid.NewGuid();
        var order = Guid.NewGuid();
        await using var context = _postgres.NewContext();
        await Store(context).TryReserveAsync(client, key, [1], order, Token);

        await Store(context).CompleteAsync(client, key, 201, """{"number":"ORD-1"}""", Token);

        await using var reader = _postgres.NewContext();
        var stored = await Store(reader).TryGetAsync(client, key, Token);
        stored.ShouldNotBeNull();
        stored.ResponseStatus.ShouldBe(201);
        stored.ResponseBody.ShouldContain("ORD-1");
        stored.OrderId.ShouldBe(order);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TryGetAsync_UnknownKey_ReturnsNull()
    {
        await using var context = _postgres.NewContext();

        var stored = await Store(context).TryGetAsync(Guid.NewGuid(), Guid.NewGuid(), Token);

        stored.ShouldBeNull();
    }

    private static IdempotencyStore Store(OrdersDbContext context) => new(context, new FixedTimeProvider(_now));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
