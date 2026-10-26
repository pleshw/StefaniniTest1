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

        orders.MapPost("/quote", QuoteOrder)
            .WithName("QuoteOrder")
            .WithSummary("Price a cart for the signed-in shopper exactly as placing the order would, without storing anything. No Idempotency-Key is needed; errors match order creation (400, 422, 503).");

        orders.MapGet("/", ListOrders)
            .WithName("ListOrders")
            .WithSummary("List orders newest first. Shoppers see only their own orders (403 for another clientId); admins see every client. Filters: clientId, status, from (inclusive), to (exclusive), q (order number or client name); keyset paging with after (the previous page's next) and limit (1-100, default 20).");

        orders.MapGet("/{id:guid}", GetOrder)
            .WithName("GetOrder")
            .WithSummary("One order with its items and totals. Another client's order is 404 for a shopper.");

        orders.MapGet("/{id:guid}/history", GetOrderHistory)
            .WithName("GetOrderHistory")
            .WithSummary("The status history of one order, oldest first. Another client's order is 404 for a shopper.");

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
            return NoClientId();
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

    private static async Task<Results<Ok<QuoteDto>, ProblemHttpResult>> QuoteOrder(
        CreateOrderRequest request, ClaimsPrincipal user, QuoteOrderHandler handler, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(OrdersAuthentication.SubjectOf(user), out var clientId))
        {
            return NoClientId();
        }

        var result = await handler.HandleAsync(
            request.ToQuery(clientId, OrdersAuthentication.NameOf(user), OrderChannel.Shop), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : ToProblem(result.Error!);
    }

    private static async Task<Results<Ok<OrderPage>, ProblemHttpResult>> ListOrders(
        ClaimsPrincipal user,
        ListOrdersHandler handler,
        CancellationToken cancellationToken,
        Guid? clientId = null,
        string? status = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? q = null,
        string? after = null,
        int? limit = null)
    {
        if (!TryGetViewer(user, out var viewer))
        {
            return NoClientId();
        }

        var result = await handler.HandleAsync(new ListOrdersQuery(viewer, clientId, status, from, to, q, after, limit), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : ToProblem(result.Error!);
    }

    private static async Task<Results<Ok<OrderDto>, ProblemHttpResult>> GetOrder(
        Guid id, ClaimsPrincipal user, GetOrderHandler handler, CancellationToken cancellationToken)
    {
        if (!TryGetViewer(user, out var viewer))
        {
            return NoClientId();
        }

        var result = await handler.HandleAsync(new GetOrderQuery(id, viewer), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : ToProblem(result.Error!);
    }

    private static async Task<Results<Ok<IReadOnlyList<OrderStatusChangeDto>>, ProblemHttpResult>> GetOrderHistory(
        Guid id, ClaimsPrincipal user, GetOrderHistoryHandler handler, CancellationToken cancellationToken)
    {
        if (!TryGetViewer(user, out var viewer))
        {
            return NoClientId();
        }

        var result = await handler.HandleAsync(new GetOrderHistoryQuery(id, viewer), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : ToProblem(result.Error!);
    }

    /// <summary>An admin sees every client (viewer null); a shopper is the client named by their sub claim.</summary>
    private static bool TryGetViewer(ClaimsPrincipal user, out Guid? viewer)
    {
        viewer = null;

        if (user.IsInRole(OrdersAuthentication.AdminRole))
        {
            return true;
        }

        if (!Guid.TryParse(OrdersAuthentication.SubjectOf(user), out var clientId))
        {
            return false;
        }

        viewer = clientId;
        return true;
    }

    private static ProblemHttpResult NoClientId() =>
        TypedResults.Problem(detail: "The signed-in user has no client id.", statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");

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
        ErrorKind.NotFound => TypedResults.Problem(detail: error.Message, statusCode: StatusCodes.Status404NotFound, title: "Not found"),
        ErrorKind.Forbidden => TypedResults.Problem(detail: error.Message, statusCode: StatusCodes.Status403Forbidden, title: "Forbidden"),
        _ => TypedResults.Problem(detail: error.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request"),
    };
}
