using System.Text;
using System.Text.Json;
using Orders.Bff.Events;
using Orders.Contracts;

namespace Orders.Bff.UnitTests;

/// <summary>The in-memory fan-out between the BFF's RabbitMQ queue and the open streams.</summary>
public sealed class EventHubTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly Guid _order = Guid.Parse("6f1c2d3e-0000-4000-8000-000000000001");

    [Fact]
    [Trait("Category", "Unit")]
    public void Notify_ReachesEveryOpenSubscription()
    {
        var hub = new EventHub(capacity: 10);
        using var first = hub.Subscribe();
        using var second = hub.Subscribe();

        hub.Notify(_order);

        first.Reader.TryRead(out var a).ShouldBeTrue();
        second.Reader.TryRead(out var b).ShouldBeTrue();
        (a, b).ShouldBe((_order, _order));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Notify_AfterDispose_SkipsThatSubscription()
    {
        var hub = new EventHub(capacity: 10);
        var gone = hub.Subscribe();
        using var open = hub.Subscribe();
        gone.Dispose();

        hub.Notify(_order);

        hub.Subscribers.ShouldBe(1);
        gone.Reader.TryRead(out _).ShouldBeFalse();
        open.Reader.TryRead(out _).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Notify_SlowSubscriberFull_KeepsTheNewestAndNeverBlocks()
    {
        var hub = new EventHub(capacity: 2);
        using var slow = hub.Subscribe();
        var orders = Enumerable.Range(1, 5).Select(i => new Guid(i, 0, 0, new byte[8])).ToList();

        orders.ForEach(hub.Notify);

        var kept = new List<Guid>();
        while (slow.Reader.TryRead(out var order))
        {
            kept.Add(order);
        }

        kept.ShouldBe(orders[^2..]);
    }

    [Theory]
    [InlineData("order.placed")]
    [InlineData("order.status-changed")]
    [Trait("Category", "Unit")]
    public void OrderIdOf_PublishedContract_ReadsTheOrderId(string routingKey)
    {
        object contract = routingKey == "order.placed"
            ? new OrderPlacedV1(_order, "ORD-1", Guid.NewGuid(), "shop", "BRL", 10m, 1, DateTimeOffset.UnixEpoch)
            : new OrderStatusChangedV1(_order, "Pending", "Confirmed", null, "system:worker", DateTimeOffset.UnixEpoch);
        var body = JsonSerializer.SerializeToUtf8Bytes(contract, contract.GetType(), _json);

        EventMessages.OrderIdOf(routingKey, body).ShouldBe(_order);
    }

    [Theory]
    [InlineData("order.unknown", """{"orderId":"6f1c2d3e-0000-4000-8000-000000000001"}""")]
    [InlineData("order.placed", "not json")]
    [InlineData("order.placed", "{}")]
    [Trait("Category", "Unit")]
    public void OrderIdOf_UnknownOrBrokenMessage_IsNull(string routingKey, string body)
    {
        EventMessages.OrderIdOf(routingKey, Encoding.UTF8.GetBytes(body)).ShouldBeNull();
    }
}
