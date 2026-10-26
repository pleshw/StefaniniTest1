using Microsoft.EntityFrameworkCore;
using Orders.Application.Ordering;
using Orders.Domain;
using Orders.Infrastructure.Persistence;

namespace Orders.IntegrationTests;

/// <summary>
/// The SQL EF Core generates for the order queries, read with <c>ToQueryString</c>: no database is contacted. The
/// Postgres-backed behaviour is in <see cref="OrderQueryPostgresTests"/>.
/// </summary>
public sealed class OrderQuerySqlTests : IDisposable
{
    private static readonly Guid _client = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly OrdersDbContext _context = new(new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql("Host=unused;Database=unused").Options);

    [Fact]
    [Trait("Category", "Unit")]
    public void ListQuery_NoFilters_OrdersByCreationDescendingThenIdWithTheLimit()
    {
        var sql = List(new OrderListFilter(null, null, null, null, null, null, 21));

        sql.ShouldContain("ORDER BY o.created_at DESC, o.id");
        sql.ShouldContain("LIMIT @p");
        sql.ShouldNotContain("WHERE");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ListQuery_EveryFilter_IsAParameterizedPredicate()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var cursor = new OrderCursor(from.AddDays(3), Guid.NewGuid());

        var sql = List(new OrderListFilter(_client, OrderStatus.OnHold, from, from.AddDays(7), "ana 50%", cursor, 21));

        sql.ShouldContain(".client_id = @");
        sql.ShouldContain("o.status = @");
        sql.ShouldContain("o.created_at >= @");
        sql.ShouldContain("o.created_at < @");
        sql.ShouldContain("o.search_vector @@ websearch_to_tsquery('simple', @");
        sql.ShouldContain("o.client_name ILIKE @");
        sql.ShouldContain("ESCAPE '\\'");
        sql.ShouldContain("o.id > @");
        sql.ShouldNotContain("ana 50%' ");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ListQuery_SearchWithLikeWildcards_EscapesThem()
    {
        var sql = List(new OrderListFilter(null, null, null, null, "50%_off", null, 21));

        sql.ShouldContain("'%50\\%\\_off%'");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ListQuery_Projection_ReadsOnlyTheSummaryColumns()
    {
        var sql = List(new OrderListFilter(null, null, null, null, null, null, 21));

        sql.ShouldNotContain("order_items");
        sql.ShouldNotContain("search_vector");
        sql.ShouldNotContain("row_version");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void HistoryQuery_OwnerGiven_ChecksTheOrderBelongsToThem()
    {
        var sql = new OrderQueries(_context).HistoryQuery(Guid.NewGuid(), _client).ToQueryString();

        sql.ShouldContain("EXISTS");
        sql.ShouldContain(".client_id = @");
        sql.ShouldContain("ORDER BY o.seq");
    }

    public void Dispose() => _context.Dispose();

    private string List(OrderListFilter filter) => new OrderQueries(_context).ListQuery(filter).ToQueryString();
}
