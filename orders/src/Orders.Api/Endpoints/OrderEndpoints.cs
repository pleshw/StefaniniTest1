using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Orders.Api.Security;
using Orders.Application;
using Orders.Application.Idempotency;
using Orders.Application.Ordering;
using Orders.Domain;

namespace Orders.Api.Endpoints;

internal static class OrderEndpoints
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/api/v1/orders")
            .WithTags("Orders")
            .RequireAuthorization();

        orders.MapPost("/", CreateOrder)
            .WithName("CreateOrder")
            .WithSummary("Place an order for the signed-in shopper. Idempotency-Key (UUID) is required: 400 without it, 422 when it was used with another body, replay of the first response when it was used with the same body.");

        return app;
    }

    internal sealed record CreateOrderRequest(string? Currency, List<CreateOrderLine>? Items);

    internal sealed record CreateOrderLine(string? Sku, int Quantity);

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> CreateOrder(
        CreateOrderRequest request,
        [FromHeader(Name = IdempotencyKey.HeaderName)] string? idempotencyKey,
        ClaimsPrincipal user,
        CreateOrderHandler handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(OrdersAuthentication.SubjectOf(user), out var clientId))
        {
            return TypedResults.Problem(
                detail: "The signed-in user has no client id.", statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");
        }

        var command = request.ToCommand(
            clientId,
            OrdersAuthentication.NameOf(user),
            clientId.ToString(),
            OrderChannel.Shop,
            idempotencyKey,
            CanonicalRequestHash.Compute(request, _json));

        return await Place(command, handler, response, cancellationToken);
    }

    /// <summary>Runs the create-order flow and writes the stored status, body and Location (same for every replay).</summary>
    internal static async Task<Results<ContentHttpResult, ProblemHttpResult>> Place(
        CreateOrderCommand command, CreateOrderHandler handler, HttpResponse response, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(command, cancellationToken);

        if (!result.IsSuccess)
        {
            return ToProblem(result.Error!);
        }

        var outcome = result.Value;
        response.Headers.Location = $"/api/v1/orders/{outcome.OrderId}";

        return TypedResults.Text(outcome.Body, "application/json", Encoding.UTF8, outcome.Status);
    }

    internal static ProblemHttpResult ToProblem(ResultError error) => error.Kind switch
    {
        ErrorKind.Unprocessable => TypedResults.Problem(detail: error.Message, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Unprocessable request"),
        ErrorKind.CatalogUnavailable => TypedResults.Problem(detail: error.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Catalog unavailable"),
        _ => TypedResults.Problem(detail: error.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request"),
    };
}
