using Orders.Domain;
using Riok.Mapperly.Abstractions;

namespace Orders.Application.Ordering;

[Mapper]
public static partial class OrderMapper
{
    [MapperIgnoreSource(nameof(Order.CreatedBy))]
    [MapperIgnoreSource(nameof(Order.UpdatedAt))]
    [MapperIgnoreSource(nameof(Order.RowVersion))]
    [MapperIgnoreSource(nameof(Order.StatusHistory))]
    [MapperIgnoreSource(nameof(Order.DomainEvents))]
    public static partial OrderDto ToDto(this Order order);

    [MapperIgnoreSource(nameof(OrderItem.LineSubtotal))]
    private static partial OrderItemDto ToDto(OrderItem item);

    private static string ChannelToString(OrderChannel channel) => channel.ToString().ToLowerInvariant();

    private static string CurrencyToString(Currency currency) => currency.Code;

    private static decimal MoneyToAmount(Money money) => money.Amount;
}
