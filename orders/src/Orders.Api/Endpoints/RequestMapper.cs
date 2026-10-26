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

    [MapProperty(nameof(AdminOrderEndpoints.CreateAdminOrderRequest.Items), nameof(CreateOrderCommand.Lines))]
    public static partial CreateOrderCommand ToCommand(
        this AdminOrderEndpoints.CreateAdminOrderRequest request,
        string createdBy,
        OrderChannel channel,
        string? idempotencyKey,
        byte[] requestHash);

    [MapProperty(nameof(OrderEndpoints.CreateOrderRequest.Items), nameof(QuoteOrderQuery.Lines))]
    public static partial QuoteOrderQuery ToQuery(
        this OrderEndpoints.CreateOrderRequest request, Guid clientId, string clientName, OrderChannel channel);

    private static partial CartLine ToCartLine(OrderEndpoints.CreateOrderLine line);

    /// <summary>A missing text field reaches the validators as empty, so it is a 400 instead of a mapping exception.</summary>
    private static string ToText(string? value) => value ?? string.Empty;
}
