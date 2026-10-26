using System.Text.Json;
using Orders.Contracts;
using Orders.Domain;
using Orders.Infrastructure.Messaging;

namespace Orders.IntegrationTests;

/// <summary>The published event contracts: their JSON never changes silently, and domain events map onto them.</summary>
public sealed class EventContractTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly Guid _order = Guid.Parse("3f9a1c2b-7d40-4e8a-b1c2-9d8e7f6a5b4c");
    private static readonly Guid _client = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset _at = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Unit")]
    public void OrderPlacedV1_Json_MatchesTheSnapshot()
    {
        var contract = new OrderPlacedV1(_order, "ORD-2026-3F9A1C2B7D40", _client, "shop", "BRL", 1234.50m, 3, _at);

        JsonSerializer.Serialize(contract, _json).ShouldBe(
            """{"orderId":"3f9a1c2b-7d40-4e8a-b1c2-9d8e7f6a5b4c","number":"ORD-2026-3F9A1C2B7D40","clientId":"11111111-1111-1111-1111-111111111111","channel":"shop","currency":"BRL","total":1234.50,"itemCount":3,"occurredAt":"2026-03-04T05:06:07+00:00"}""");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OrderStatusChangedV1_Json_MatchesTheSnapshot()
    {
        var contract = new OrderStatusChangedV1(_order, "Pending", "OnHold", "Large order", "system:worker", _at);

        JsonSerializer.Serialize(contract, _json).ShouldBe(
            """{"orderId":"3f9a1c2b-7d40-4e8a-b1c2-9d8e7f6a5b4c","from":"Pending","to":"OnHold","reason":"Large order","actor":"system:worker","occurredAt":"2026-03-04T05:06:07+00:00"}""");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OrderPlacedV1_Json_RoundTrips()
    {
        var contract = new OrderPlacedV1(_order, "ORD-2026-3F9A1C2B7D40", _client, "admin", "USD", 10m, 1, _at);

        JsonSerializer.Deserialize<OrderPlacedV1>(JsonSerializer.Serialize(contract, _json), _json).ShouldBe(contract);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToContract_OrderPlaced_MapsMoneyAndChannelToPrimitives()
    {
        var placed = new OrderPlaced(_order, "ORD-2026-3F9A1C2B7D40", _client, OrderChannel.Admin, Money.Create(1234.50m, Currency.Create("BRL")), 3, _at);

        placed.ToContract().ShouldBe(new OrderPlacedV1(_order, "ORD-2026-3F9A1C2B7D40", _client, "admin", "BRL", 1234.50m, 3, _at));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToContract_OrderStatusChanged_MapsStatusesToNames()
    {
        var changed = new OrderStatusChanged(_order, OrderStatus.Pending, OrderStatus.OnHold, "Large order", "system:worker", _at);

        changed.ToContract().ShouldBe(new OrderStatusChangedV1(_order, "Pending", "OnHold", "Large order", "system:worker", _at));
    }
}
