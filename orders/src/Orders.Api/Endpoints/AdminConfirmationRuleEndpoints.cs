using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Orders.Api.Security;
using Orders.Application;
using Orders.Application.Confirmation;

namespace Orders.Api.Endpoints;

internal static class AdminConfirmationRuleEndpoints
{
    private const string _ifMatch = "If-Match";

    public static IEndpointRouteBuilder MapAdminConfirmationRuleEndpoints(this IEndpointRouteBuilder app)
    {
        var rules = app.MapGroup("/api/v1/admin/confirmation-rules")
            .WithTags("Admin confirmation rules")
            .RequireAuthorization(OrdersAuthentication.AdminPolicy);

        rules.MapGet("/", ListRules)
            .WithName("ListConfirmationRules")
            .WithSummary("The latest version of every confirmation rule, lowest priority first. Filter: active (true or false).");

        rules.MapPost("/", CreateRule)
            .WithName("CreateConfirmationRule")
            .WithSummary("Create a rule (version 1). A new order that matches an active Hold rule goes OnHold instead of Confirmed. Conditions: TotalAbove, FirstOrderOfClient and ChannelIs, at least one; 400 with every message otherwise. The ETag of the response is the version.");

        rules.MapGet("/{id:guid}", GetRule)
            .WithName("GetConfirmationRule")
            .WithSummary("The latest version of a rule; the ETag is its version.");

        rules.MapPut("/{id:guid}", ReplaceRule)
            .WithName("ReplaceConfirmationRule")
            .WithSummary("Replace a rule by storing a new version. If-Match (the ETag of the version being replaced) is required: 428 without it, 412 when it is not the latest version. The Worker uses the new version for the orders it decides after its short rule cache expires.");

        rules.MapPost("/{id:guid}/deactivate", DeactivateRule)
            .WithName("DeactivateConfirmationRule")
            .WithSummary("Store a new, inactive version of a rule. If-Match is optional; when sent and stale the answer is 412.");

        return app;
    }

    /// <param name="Action">Hold, the only action there is; Hold when omitted.</param>
    /// <param name="Conditions">A JSON array of conditions combined with AND, at least one.</param>
    internal sealed record ConfirmationRuleRequest(string? Name, string? Action, JsonElement? Conditions, int Priority, bool IsActive = true);

    private static async Task<Results<Ok<IReadOnlyList<ConfirmationRuleDto>>, ProblemHttpResult>> ListRules(
        ListConfirmationRulesHandler handler, CancellationToken cancellationToken, bool? active = null)
    {
        var result = await handler.HandleAsync(new ListConfirmationRulesQuery(active), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : OrderEndpoints.ToProblem(result.Error!);
    }

    private static async Task<Results<Ok<ConfirmationRuleDto>, ProblemHttpResult>> GetRule(
        Guid id, GetConfirmationRuleHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
        Respond(await handler.HandleAsync(new GetConfirmationRuleQuery(id), cancellationToken), response);

    private static async Task<Results<Created<ConfirmationRuleDto>, ProblemHttpResult>> CreateRule(
        ConfirmationRuleRequest request, ClaimsPrincipal user, CreateConfirmationRuleHandler handler, HttpResponse response, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new CreateConfirmationRuleCommand(Definition(request), OrdersAuthentication.SubjectOf(user) ?? string.Empty), cancellationToken);

        if (!result.IsSuccess)
        {
            return OrderEndpoints.ToProblem(result.Error!);
        }

        response.Headers.ETag = EntityTag.For(result.Value.Version);

        return TypedResults.Created($"/api/v1/admin/confirmation-rules/{result.Value.Id}", result.Value);
    }

    private static async Task<Results<Ok<ConfirmationRuleDto>, ProblemHttpResult>> ReplaceRule(
        Guid id,
        ConfirmationRuleRequest request,
        [FromHeader(Name = _ifMatch)] string? ifMatch,
        ClaimsPrincipal user,
        ReplaceConfirmationRuleHandler handler,
        HttpResponse response,
        CancellationToken cancellationToken) =>
        Respond(
            await handler.HandleAsync(
                new ReplaceConfirmationRuleCommand(id, ifMatch, Definition(request), OrdersAuthentication.SubjectOf(user) ?? string.Empty), cancellationToken),
            response);

    private static async Task<Results<Ok<ConfirmationRuleDto>, ProblemHttpResult>> DeactivateRule(
        Guid id,
        [FromHeader(Name = _ifMatch)] string? ifMatch,
        ClaimsPrincipal user,
        DeactivateConfirmationRuleHandler handler,
        HttpResponse response,
        CancellationToken cancellationToken) =>
        Respond(
            await handler.HandleAsync(new DeactivateConfirmationRuleCommand(id, ifMatch, OrdersAuthentication.SubjectOf(user) ?? string.Empty), cancellationToken),
            response);

    private static ConfirmationRuleDefinition Definition(ConfirmationRuleRequest request) => new(
        request.Name, request.Action ?? nameof(Orders.Domain.Confirmation.ConfirmationAction.Hold), request.Conditions?.GetRawText(), request.Priority, request.IsActive);

    private static Results<Ok<ConfirmationRuleDto>, ProblemHttpResult> Respond(Result<ConfirmationRuleDto> result, HttpResponse response)
    {
        if (!result.IsSuccess)
        {
            return OrderEndpoints.ToProblem(result.Error!);
        }

        response.Headers.ETag = EntityTag.For(result.Value.Version);

        return TypedResults.Ok(result.Value);
    }
}
