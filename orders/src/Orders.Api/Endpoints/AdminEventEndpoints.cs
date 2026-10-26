using Microsoft.AspNetCore.Http.HttpResults;
using Orders.Api.Security;
using Orders.Application.Events;

namespace Orders.Api.Endpoints;

internal static class AdminEventEndpoints
{
    public static IEndpointRouteBuilder MapAdminEventEndpoints(this IEndpointRouteBuilder app)
    {
        var events = app.MapGroup("/api/v1/admin/events")
            .WithTags("Admin events")
            .RequireAuthorization(OrdersAuthentication.AdminPolicy);

        events.MapGet("/", List)
            .WithName("ListEvents")
            .WithSummary("The event log, oldest first: status changes of orders and dead letter replays, kept 30 days. Filters: type (exact, such as OrderStatusChanged.v1) and orderId; paging: after (the previous page's next) and limit (1-100, default 50).");

        return app;
    }

    private static async Task<Results<Ok<EventPage>, ProblemHttpResult>> List(
        ListEventsHandler handler, CancellationToken cancellationToken, string? after = null, int? limit = null, string? type = null, Guid? orderId = null)
    {
        var result = await handler.HandleAsync(new ListEventsQuery(after, limit, type, orderId), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : OrderEndpoints.ToProblem(result.Error!);
    }
}
