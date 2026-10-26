namespace Orders.Domain.UnitTests;

public sealed class OrderRestoreTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Restore_StoredFields_RebuildsTheOrderWithoutEvents()
    {
        var original = OrderBuilder.InStatus(OrderStatus.Cancelled);
        var updatedAt = OrderBuilder.Now.AddHours(1);

        var restored = Order.Restore(
            original.Id,
            original.Number,
            original.Client,
            original.CreatedBy,
            original.Channel,
            original.Status,
            original.Items,
            original.StatusHistory,
            original.CreatedAt,
            updatedAt,
            original.RowVersion);

        restored.Status.ShouldBe(OrderStatus.Cancelled);
        restored.RowVersion.ShouldBe(original.RowVersion);
        restored.UpdatedAt.ShouldBe(updatedAt);
        restored.Total.ShouldBe(original.Total);
        restored.StatusHistory.ShouldBe(original.StatusHistory);
        restored.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Restore_NoItems_Throws()
    {
        var original = OrderBuilder.Pending();

        Should.Throw<DomainException>(() => Order.Restore(
            original.Id, original.Number, original.Client, original.CreatedBy, original.Channel, original.Status,
            [], original.StatusHistory, original.CreatedAt, original.UpdatedAt, original.RowVersion));
    }
}
