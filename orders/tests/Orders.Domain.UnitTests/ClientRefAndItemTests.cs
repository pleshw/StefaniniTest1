namespace Orders.Domain.UnitTests;

public sealed class ClientRefAndItemTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ClientRef_Create_TrimsTheName()
    {
        var client = ClientRef.Create(OrderBuilder.ClientId, "  Ana ");

        client.Name.ShouldBe("Ana");
        client.Id.ShouldBe(OrderBuilder.ClientId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClientRef_Create_EmptyIdOrName_Throws()
    {
        Should.Throw<DomainException>(() => ClientRef.Create(Guid.Empty, "Ana"));
        Should.Throw<DomainException>(() => ClientRef.Create(OrderBuilder.ClientId, " "));
        Should.Throw<DomainException>(() => ClientRef.Create(OrderBuilder.ClientId, null));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OrderItem_Create_ComputesTheLineTotalAfterTheDiscount()
    {
        var item = OrderItem.Create(" sku-1 ", " Keyboard ", OrderBuilder.BrlAmount(19.99m), 4, 3, OrderBuilder.BrlAmount(5m));

        item.Sku.ShouldBe("SKU-1");
        item.ProductName.ShouldBe("Keyboard");
        item.CatalogVersion.ShouldBe(4);
        item.LineSubtotal.Amount.ShouldBe(59.97m);
        item.LineDiscount.Amount.ShouldBe(5m);
        item.LineTotal.Amount.ShouldBe(54.97m);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OrderItem_Create_NoDiscount_DefaultsToZero()
    {
        var item = OrderItem.Create("SKU-1", "Keyboard", OrderBuilder.BrlAmount(10m), 1, 2);

        item.LineDiscount.Amount.ShouldBe(0m);
        item.LineTotal.Amount.ShouldBe(20m);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(0)]
    [InlineData(-1)]
    public void OrderItem_Create_QuantityNotPositive_Throws(int quantity)
    {
        Should.Throw<DomainException>(() => OrderBuilder.Item(quantity: quantity));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OrderItem_Create_InvalidSkuOrName_Throws()
    {
        Should.Throw<DomainException>(() => OrderItem.Create(" ", "Keyboard", OrderBuilder.BrlAmount(1m), 1, 1));
        Should.Throw<DomainException>(() => OrderItem.Create(new string('A', 33), "Keyboard", OrderBuilder.BrlAmount(1m), 1, 1));
        Should.Throw<DomainException>(() => OrderItem.Create("SKU", " ", OrderBuilder.BrlAmount(1m), 1, 1));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OrderItem_Create_DiscountAboveTheLineSubtotal_Throws()
    {
        Should.Throw<DomainException>(() => OrderBuilder.Item(price: 10m, quantity: 1, discount: 10.01m));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OrderItem_Create_DiscountEqualToTheLineSubtotal_GivesAZeroLine()
    {
        OrderBuilder.Item(price: 10m, quantity: 1, discount: 10m).LineTotal.Amount.ShouldBe(0m);
    }
}
