namespace Orders.Domain.UnitTests;

internal static class OrderBuilder
{
    public static readonly DateTimeOffset Now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    public static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static Currency Brl => Currency.Create("BRL");

    public static Currency Usd => Currency.Create("USD");

    public static Money BrlAmount(decimal amount) => Money.Create(amount, Brl);

    public static Money UsdAmount(decimal amount) => Money.Create(amount, Usd);

    public static OrderItem Item(string sku = "SKU-1", decimal price = 10m, int quantity = 1, decimal discount = 0m) =>
        OrderItem.Create(sku, "Keyboard", BrlAmount(price), 3, quantity, BrlAmount(discount));

    public static Order Pending(params OrderItem[] items) => Order.Place(
        Guid.NewGuid(),
        "ORD-2026-000001",
        ClientRef.Create(ClientId, "Ana"),
        "shopper-1",
        OrderChannel.Shop,
        items.Length == 0 ? [Item()] : items,
        Now);

    /// <summary>An order walked to <paramref name="status"/> through allowed transitions only.</summary>
    public static Order InStatus(OrderStatus status)
    {
        var order = Pending();

        switch (status)
        {
            case OrderStatus.OnHold:
                order.Hold("system:worker", "rule", Now);
                break;
            case OrderStatus.Confirmed:
                order.Confirm("system:worker", Now);
                break;
            case OrderStatus.Blocked:
                order.Block("admin-1", "fraud", Now);
                break;
            case OrderStatus.Failed:
                order.Fail("system:worker", "catalog down", Now);
                break;
            case OrderStatus.Cancelled:
                order.Confirm("system:worker", Now);
                order.Cancel("admin-1", "customer asked", Now);
                break;
        }

        return order;
    }
}
