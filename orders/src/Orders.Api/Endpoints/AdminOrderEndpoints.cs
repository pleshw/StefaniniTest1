using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Orders.Api.Security;
using Orders.Application;
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

        orders.MapPost("/{id:guid}/confirm", Confirm)
            .WithName("ConfirmOrder")
            .WithSummary(ActionSummary("Confirm", "Pending or OnHold", reasonRequired: false));

        orders.MapPost("/{id:guid}/hold", Hold)
            .WithName("HoldOrder")
            .WithSummary(ActionSummary("Put on hold", "Pending", reasonRequired: true));

        orders.MapPost("/{id:guid}/block", Block)
            .WithName("BlockOrder")
            .WithSummary(ActionSummary("Block", "Pending or OnHold", reasonRequired: true));

        orders.MapPost("/{id:guid}/cancel", Cancel)
            .WithName("CancelOrder")
            .WithSummary(ActionSummary("Cancel", "Confirmed", reasonRequired: true));

        return app;
    }

    private static string ActionSummary(string verb, string from, bool reasonRequired) =>
        $"{verb} an order that is {from}. If-Match (the order's ETag, its row version) is required: 428 without it, 412 when it is stale. " +
        (reasonRequired ? "A reason is required (400). " : "A reason is optional. ") +
        "Any other status is 409 invalid-transition. The history records the admin as actor and OrderStatusChanged.v1 is published.";

    /// <param name="Reason">Required to hold, block or cancel; at most 500 characters.</param>
    internal sealed record StatusChangeRequest(string? Reason);

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

    private static Task<Results<Ok<OrderDto>, ProblemHttpResult>> Confirm(
        Guid id, StatusChangeRequest? request, [FromHeader(Name = "If-Match")] string? ifMatch, ClaimsPrincipal user, ChangeOrderStatusHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
        Change(id, OrderAction.Confirm, request, ifMatch, user, handler, response, cancellationToken);

    private static Task<Results<Ok<OrderDto>, ProblemHttpResult>> Hold(
        Guid id, StatusChangeRequest? request, [FromHeader(Name = "If-Match")] string? ifMatch, ClaimsPrincipal user, ChangeOrderStatusHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
        Change(id, OrderAction.Hold, request, ifMatch, user, handler, response, cancellationToken);

    private static Task<Results<Ok<OrderDto>, ProblemHttpResult>> Block(
        Guid id, StatusChangeRequest? request, [FromHeader(Name = "If-Match")] string? ifMatch, ClaimsPrincipal user, ChangeOrderStatusHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
        Change(id, OrderAction.Block, request, ifMatch, user, handler, response, cancellationToken);

    private static Task<Results<Ok<OrderDto>, ProblemHttpResult>> Cancel(
        Guid id, StatusChangeRequest? request, [FromHeader(Name = "If-Match")] string? ifMatch, ClaimsPrincipal user, ChangeOrderStatusHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
        Change(id, OrderAction.Cancel, request, ifMatch, user, handler, response, cancellationToken);

    private static async Task<Results<Ok<OrderDto>, ProblemHttpResult>> Change(
        Guid id,
        OrderAction action,
        StatusChangeRequest? request,
        string? ifMatch,
        ClaimsPrincipal user,
        ChangeOrderStatusHandler handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new ChangeOrderStatusCommand(id, action, ifMatch, request?.Reason, OrdersAuthentication.SubjectOf(user) ?? string.Empty), cancellationToken);

        if (!result.IsSuccess)
        {
            return OrderEndpoints.ToProblem(result.Error!);
        }

        response.Headers.ETag = EntityTag.For(result.Value.RowVersion);

        return TypedResults.Ok(result.Value);
    }
}
