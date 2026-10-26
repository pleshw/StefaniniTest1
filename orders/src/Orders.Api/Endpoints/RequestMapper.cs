using Orders.Application.Ordering;
using Orders.Application.Pricing;
using Orders.Domain;
using Riok.Mapperly.Abstractions;

namespace Orders.Api.Endpoints;

/// <summary>Turns HTTP request bodies, plus the header and user values, into application commands.</summary>
[Mapper]
internal static partial class RequestMapper
{
    [MapProperty(nameof(OrderEndpoints.CreateOrderRequest.Items), nameof(CreateOrderCommand.Lines))]
    public static partial CreateOrderCommand ToCommand(
        this OrderEndpoints.CreateOrderRequest request,
        Guid clientId,
        string clientName,
        string createdBy,
        OrderChannel channel,
        string? idempotencyKey,
        byte[] requestHash);

    private static partial CartLine ToCartLine(OrderEndpoints.CreateOrderLine line);
}
