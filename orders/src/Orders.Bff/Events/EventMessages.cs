using System.Text.Json;
using Orders.Contracts;

namespace Orders.Bff.Events;

/// <summary>Reads which order a message on <c>orders.events</c> is about, from its versioned contract.</summary>
internal static class EventMessages
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static Guid? OrderIdOf(string routingKey, ReadOnlyMemory<byte> body)
    {
        try
        {
            var orderId = routingKey switch
            {
                "order.placed" => JsonSerializer.Deserialize<OrderPlacedV1>(body.Span, _json)?.OrderId,
                "order.status-changed" => JsonSerializer.Deserialize<OrderStatusChangedV1>(body.Span, _json)?.OrderId,
                _ => null,
            };

            return orderId == Guid.Empty ? null : orderId;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
