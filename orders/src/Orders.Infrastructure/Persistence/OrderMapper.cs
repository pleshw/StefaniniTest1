using Orders.Domain;
using Riok.Mapperly.Abstractions;

namespace Orders.Infrastructure.Persistence;

/// <summary>
/// Maps orders to and from their PostgreSQL records. Orders are rebuilt through <see cref="Order.Restore"/> so the
/// domain invariants run on every read.
/// </summary>
[Mapper]
internal static partial class OrderMapper
{
    public static OrderRecord ToRecord(this Order order)
    {
        var record = order.ToHeaderRecord();
        record.Items.AddRange(order.Items.Select((item, index) => item.ToRecord(order.Id, index + 1)));
        record.History.AddRange(order.StatusHistory.Select((change, index) => change.ToRecord(order.Id, index + 1)));

        return record;
    }

    [MapperIgnoreSource(nameof(OrderItem.LineSubtotal))]
    public static partial OrderItemRecord ToRecord(this OrderItem item, Guid orderId, int lineNo);

    [MapProperty(nameof(OrderStatusChange.From), nameof(OrderStatusHistoryRecord.FromStatus))]
    [MapProperty(nameof(OrderStatusChange.To), nameof(OrderStatusHistoryRecord.ToStatus))]
    [MapperIgnoreTarget(nameof(OrderStatusHistoryRecord.TraceId))]
    public static partial OrderStatusHistoryRecord ToRecord(this OrderStatusChange change, Guid orderId, int seq);

    public static Order ToDomain(this OrderRecord record)
    {
        var currency = Currency.Create(record.Currency);

        return Order.Restore(
            record.Id,
            record.Number,
            ClientRef.Create(record.ClientId, record.ClientName),
            record.CreatedBy,
            record.Channel,
            record.Status,
            record.Items.OrderBy(item => item.LineNo).Select(item => OrderItem.Create(
                item.Sku,
                item.ProductName,
                Money.Create(item.UnitPrice, currency),
                item.CatalogVersion,
                item.Quantity,
                Money.Create(item.LineDiscount, currency))),
            record.History.OrderBy(change => change.Seq).Select(change => new OrderStatusChange(
                change.FromStatus, change.ToStatus, change.Reason, change.Actor, change.ChangedAt)),
            record.CreatedAt,
            record.UpdatedAt,
            record.RowVersion);
    }

    [MapperIgnoreSource(nameof(Order.Items))]
    [MapperIgnoreSource(nameof(Order.StatusHistory))]
    [MapperIgnoreSource(nameof(Order.DomainEvents))]
    [MapperIgnoreTarget(nameof(OrderRecord.Items))]
    [MapperIgnoreTarget(nameof(OrderRecord.History))]
    [MapperIgnoreTarget(nameof(OrderRecord.SearchVector))]
    private static partial OrderRecord ToHeaderRecord(this Order order);

    private static string CurrencyToString(Currency currency) => currency.Code;

    private static decimal MoneyToAmount(Money money) => money.Amount;
}
