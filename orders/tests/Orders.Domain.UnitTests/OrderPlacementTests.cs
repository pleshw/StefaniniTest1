namespace Orders.Domain.UnitTests;

public sealed class OrderPlacementTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Place_ValidItems_CalculatesCountAndTotals()
    {
        var order = OrderBuilder.Pending(
            OrderBuilder.Item("A", price: 19.99m, quantity: 3, discount: 5m),
            OrderBuilder.Item("B", price: 0.335m, quantity: 3));

        order.ItemCount.ShouldBe(6);
        order.Subtotal.Amount.ShouldBe(60.99m);
        order.DiscountTotal.Amount.ShouldBe(5m);
        order.Total.Amount.ShouldBe(55.99m);
        order.Currency.Code.ShouldBe("BRL");
        order.Items.Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Place_ValidItems_StartsPendingWithTheFirstHistoryEntryAndOrderPlaced()
    {
        var order = OrderBuilder.Pending();

        order.Status.ShouldBe(OrderStatus.Pending);
        order.RowVersion.ShouldBe(0);
        order.CreatedAt.ShouldBe(OrderBuilder.Now);
        order.UpdatedAt.ShouldBe(OrderBuilder.Now);
        order.CreatedBy.ShouldBe("shopper-1");
        order.Channel.ShouldBe(OrderChannel.Shop);
        order.Client.Name.ShouldBe("Ana");
        order.StatusHistory.ShouldBe([new OrderStatusChange(null, OrderStatus.Pending, null, "shopper-1", OrderBuilder.Now)]);
        var placed = order.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<OrderPlaced>();
        placed.OrderId.ShouldBe(order.Id);
        placed.Number.ShouldBe("ORD-2026-000001");
        placed.ClientId.ShouldBe(OrderBuilder.ClientId);
        placed.Total.ShouldBe(order.Total);
        placed.ItemCount.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Place_NoItems_Throws()
    {
        Should.Throw<DomainException>(() => Place(Guid.NewGuid(), "ORD-1", "u", []));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Place_MixedCurrencyItems_Throws()
    {
        var usdItem = OrderItem.Create("B", "Mouse", OrderBuilder.UsdAmount(5m), 1, 1);

        Should.Throw<DomainException>(() => OrderBuilder.Pending(OrderBuilder.Item("A"), usdItem));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Place_BlankIdentifiers_Throws()
    {
        OrderItem[] items = [OrderBuilder.Item()];

        Should.Throw<DomainException>(() => Place(Guid.Empty, "ORD-1", "u", items));
        Should.Throw<DomainException>(() => Place(Guid.NewGuid(), " ", "u", items));
        Should.Throw<DomainException>(() => Place(Guid.NewGuid(), "ORD-1", " ", items));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClearDomainEvents_AfterPlacing_EmptiesTheList()
    {
        var order = OrderBuilder.Pending();

        order.ClearDomainEvents();

        order.DomainEvents.ShouldBeEmpty();
    }

    private static Order Place(Guid id, string number, string createdBy, IEnumerable<OrderItem> items) => Order.Place(
        id, number, ClientRef.Create(OrderBuilder.ClientId, "Ana"), createdBy, OrderChannel.Shop, items, OrderBuilder.Now);
}
