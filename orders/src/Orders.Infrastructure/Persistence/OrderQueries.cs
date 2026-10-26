using Microsoft.EntityFrameworkCore;
using Orders.Application.Ordering;
using Orders.Domain;
using Riok.Mapperly.Abstractions;

namespace Orders.Infrastructure.Persistence;

/// <summary>
/// Order reads: <c>AsNoTracking</c> projections, keyset paging on <c>(created_at DESC, id)</c> to match the
/// <c>ix_orders_client_created</c> and <c>ix_orders_status_created</c> indexes, full text through the generated
/// <c>search_vector</c> and partial client names through the trigram index.
/// </summary>
internal sealed class OrderQueries(OrdersDbContext context) : IOrderQueries
{
    private const string _textSearchConfiguration = "simple";
    private const string _likeEscape = "\\";

    public async Task<IReadOnlyList<OrderSummaryDto>> ListAsync(OrderListFilter filter, CancellationToken cancellationToken) =>
        await ListQuery(filter).ToListAsync(cancellationToken);

    public async Task<OrderDto?> GetAsync(Guid id, Guid? ownerId, CancellationToken cancellationToken) =>
        await context.Orders
            .AsNoTracking()
            .Where(order => order.Id == id && (ownerId == null || order.ClientId == ownerId))
            .ProjectToDetail()
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<OrderStatusChangeDto>?> GetHistoryAsync(Guid id, Guid? ownerId, CancellationToken cancellationToken)
    {
        // Every order has at least its first history row, so no rows means no visible order.
        var history = await HistoryQuery(id, ownerId).ToListAsync(cancellationToken);

        return history.Count == 0 ? null : history;
    }

    public async Task<bool> ClientHasOrdersAsync(Guid clientId, CancellationToken cancellationToken) =>
        await context.Orders.AsNoTracking().AnyAsync(order => order.ClientId == clientId, cancellationToken);

    internal IQueryable<OrderSummaryDto> ListQuery(OrderListFilter filter)
    {
        var orders = context.Orders.AsNoTracking();

        if (filter.ClientId is { } clientId)
        {
            orders = orders.Where(order => order.ClientId == clientId);
        }

        if (filter.Status is { } status)
        {
            orders = orders.Where(order => order.Status == status);
        }

        if (filter.From is { } from)
        {
            orders = orders.Where(order => order.CreatedAt >= from);
        }

        if (filter.To is { } to)
        {
            orders = orders.Where(order => order.CreatedAt < to);
        }

        if (filter.Search is { } search)
        {
            var pattern = $"%{EscapeLike(search)}%";
            orders = orders.Where(order =>
                order.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery(_textSearchConfiguration, search))
                || EF.Functions.ILike(order.ClientName, pattern, _likeEscape));
        }

        if (filter.After is { } after)
        {
            orders = orders.Where(order => order.CreatedAt < after.CreatedAt
                || (order.CreatedAt == after.CreatedAt && order.Id.CompareTo(after.Id) > 0));
        }

        return orders
            .OrderByDescending(order => order.CreatedAt)
            .ThenBy(order => order.Id)
            .Take(filter.Take)
            .ProjectToSummary();
    }

    internal IQueryable<OrderStatusChangeDto> HistoryQuery(Guid id, Guid? ownerId) =>
        context.OrderStatusHistory
            .AsNoTracking()
            .Where(change => change.OrderId == id
                && context.Orders.Any(order => order.Id == id && (ownerId == null || order.ClientId == ownerId)))
            .OrderBy(change => change.Seq)
            .ProjectToHistory();

    private static string EscapeLike(string text) =>
        text.Replace(_likeEscape, _likeEscape + _likeEscape, StringComparison.Ordinal)
            .Replace("%", _likeEscape + "%", StringComparison.Ordinal)
            .Replace("_", _likeEscape + "_", StringComparison.Ordinal);
}

/// <summary>Queryable projections: Mapperly writes the <c>Select</c> expressions EF Core translates to SQL.</summary>
[Mapper]
internal static partial class OrderQueryMapper
{
    public static partial IQueryable<OrderSummaryDto> ProjectToSummary(this IQueryable<OrderRecord> orders);

    public static partial IQueryable<OrderDto> ProjectToDetail(this IQueryable<OrderRecord> orders);

    public static partial IQueryable<OrderStatusChangeDto> ProjectToHistory(this IQueryable<OrderStatusHistoryRecord> history);

    [MapPropertyFromSource(nameof(OrderSummaryDto.Client), Use = nameof(ToClient))]
    [MapperIgnoreSource(nameof(OrderRecord.ClientId))]
    [MapperIgnoreSource(nameof(OrderRecord.ClientName))]
    [MapperIgnoreSource(nameof(OrderRecord.CreatedBy))]
    [MapperIgnoreSource(nameof(OrderRecord.UpdatedAt))]
    [MapperIgnoreSource(nameof(OrderRecord.RowVersion))]
    [MapperIgnoreSource(nameof(OrderRecord.SearchVector))]
    [MapperIgnoreSource(nameof(OrderRecord.Items))]
    [MapperIgnoreSource(nameof(OrderRecord.History))]
    private static partial OrderSummaryDto ToSummary(OrderRecord order);

    [MapPropertyFromSource(nameof(OrderSummaryDto.Client), Use = nameof(ToClient))]
    [MapperIgnoreSource(nameof(OrderRecord.ClientId))]
    [MapperIgnoreSource(nameof(OrderRecord.ClientName))]
    [MapperIgnoreSource(nameof(OrderRecord.CreatedBy))]
    [MapperIgnoreSource(nameof(OrderRecord.UpdatedAt))]
    [MapperIgnoreSource(nameof(OrderRecord.RowVersion))]
    [MapperIgnoreSource(nameof(OrderRecord.SearchVector))]
    [MapperIgnoreSource(nameof(OrderRecord.History))]
    private static partial OrderDto ToDetail(OrderRecord order);

    [MapProperty(nameof(OrderStatusHistoryRecord.FromStatus), nameof(OrderStatusChangeDto.From))]
    [MapProperty(nameof(OrderStatusHistoryRecord.ToStatus), nameof(OrderStatusChangeDto.To))]
    [MapperIgnoreSource(nameof(OrderStatusHistoryRecord.OrderId))]
    [MapperIgnoreSource(nameof(OrderStatusHistoryRecord.TraceId))]
    private static partial OrderStatusChangeDto ToHistory(OrderStatusHistoryRecord change);

    [MapperIgnoreSource(nameof(OrderItemRecord.OrderId))]
    [MapperIgnoreSource(nameof(OrderItemRecord.LineNo))]
    private static partial OrderItemDto ToDto(OrderItemRecord item);

    private static ClientDto ToClient(OrderRecord order) => new(order.ClientId, order.ClientName);

    private static string ChannelToString(OrderChannel channel) => channel == OrderChannel.Shop ? "shop" : "admin";
}
