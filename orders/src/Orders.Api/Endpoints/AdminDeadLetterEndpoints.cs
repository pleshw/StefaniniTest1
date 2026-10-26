using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Orders.Api.Security;
using Orders.Application.DeadLetters;

namespace Orders.Api.Endpoints;

internal static class AdminDeadLetterEndpoints
{
    public static IEndpointRouteBuilder MapAdminDeadLetterEndpoints(this IEndpointRouteBuilder app)
    {
        var letters = app.MapGroup("/api/v1/admin/dead-letters")
            .WithTags("Admin dead letters")
            .RequireAuthorization(OrdersAuthentication.AdminPolicy);

        letters.MapGet("/", List)
            .WithName("ListDeadLetters")
            .WithSummary("Look at the messages parked in orders.dlq (oldest first, up to limit, 1-100, default 100) with the failure headers: exception type and message, attempts. They stay in the queue. 503 when the broker cannot be reached.");

        letters.MapPost("/{messageId}/replay", Replay)
            .WithName("ReplayDeadLetter")
            .WithSummary("Send a parked message back through the exchange it came through, under a new message id with an x-replayed-by header, and remove it from orders.dlq. The replay is written to the event log of its order. 404 when no message has that id, 422 when the message names no existing order, 503 when the broker cannot be reached.");

        return app;
    }

    private static async Task<Results<Ok<IReadOnlyList<DeadLetterDto>>, ProblemHttpResult>> List(
        ListDeadLettersHandler handler, CancellationToken cancellationToken, int? limit = null)
    {
        var result = await handler.HandleAsync(new ListDeadLettersQuery(limit), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : OrderEndpoints.ToProblem(result.Error!);
    }

    private static async Task<Results<Ok<DeadLetterReplay>, ProblemHttpResult>> Replay(
        string messageId, ClaimsPrincipal user, ReplayDeadLetterHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ReplayDeadLetterCommand(messageId, OrdersAuthentication.SubjectOf(user) ?? string.Empty), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : OrderEndpoints.ToProblem(result.Error!);
    }
}
