using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Orders.Api.Security;
using Orders.Application.Idempotency;
using Orders.Application.Ordering;
using Orders.Domain;

namespace Orders.Api.Endpoints;

internal static class AdminOrderEndpoints
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAdminOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/api/v1/admin/orders")
            .WithTags("Admin orders")
            .RequireAuthorization(OrdersAuthentication.AdminPolicy);

        orders.MapPost("/", CreateOrder)
            .WithName("CreateOrderForClient")
            .WithSummary("Place an order on behalf of a client (channel admin). Idempotency-Key (UUID) is required and scoped by the target client: 400 without it, 422 when that client's key was used with another body, replay of the first response when it was used with the same body.");

        return app;
    }

    internal sealed record CreateAdminOrderRequest(Guid ClientId, string? ClientName, string? Currency, List<OrderEndpoints.CreateOrderLine>? Items);

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> CreateOrder(
        CreateAdminOrderRequest request,
        [FromHeader(Name = IdempotencyKey.HeaderName)] string? idempotencyKey,
        ClaimsPrincipal user,
        CreateOrderHandler handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var command = request.ToCommand(
            OrdersAuthentication.SubjectOf(user) ?? string.Empty,
            OrderChannel.Admin,
            idempotencyKey,
            CanonicalRequestHash.Compute(request, _json));

        return await OrderEndpoints.Place(command, handler, response, cancellationToken);
    }
}
