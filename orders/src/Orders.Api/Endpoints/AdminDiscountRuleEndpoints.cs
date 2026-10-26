using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Orders.Api.Security;
using Orders.Application;
using Orders.Application.Discounts;

namespace Orders.Api.Endpoints;

internal static class AdminDiscountRuleEndpoints
{
    private const string _ifMatch = "If-Match";

    public static IEndpointRouteBuilder MapAdminDiscountRuleEndpoints(this IEndpointRouteBuilder app)
    {
        var rules = app.MapGroup("/api/v1/admin/discount-rules")
            .WithTags("Admin discount rules")
            .RequireAuthorization(OrdersAuthentication.AdminPolicy);

        rules.MapGet("/", ListRules)
            .WithName("ListDiscountRules")
            .WithSummary("The latest version of every discount rule, lowest priority first. Filter: active (true or false).");

        rules.MapPost("/", CreateRule)
            .WithName("CreateDiscountRule")
            .WithSummary("Create a rule (version 1). The parameters are checked by the rule type and the conditions by their kinds: 400 with every message otherwise. The ETag of the response is the version.");

        rules.MapGet("/{id:guid}", GetRule)
            .WithName("GetDiscountRule")
            .WithSummary("The latest version of a rule; the ETag is its version.");

        rules.MapPut("/{id:guid}", ReplaceRule)
            .WithName("ReplaceDiscountRule")
            .WithSummary("Replace a rule by storing a new version. If-Match (the ETag of the version being replaced) is required: 428 without it, 412 when it is not the latest version. Orders already placed keep the version they used.");

        rules.MapPost("/{id:guid}/deactivate", DeactivateRule)
            .WithName("DeactivateDiscountRule")
            .WithSummary("Store a new, inactive version of a rule. If-Match is optional; when sent and stale the answer is 412.");

        rules.MapPost("/preview", PreviewRule)
            .WithName("PreviewDiscountRule")
            .WithSummary("Price a sample cart exactly as a quote would, optionally with a rule that is not stored yet (replacing the stored rule named by ruleId). Nothing is stored. Without clientId the sample client has no orders, so first-order rules apply.");

        return app;
    }

    /// <param name="Type">A discount type name: Percentage, FixedAmount, BuyXGetY or Tiered.</param>
    /// <param name="Parameters">A JSON object whose fields depend on the type; unknown fields are rejected.</param>
    /// <param name="Conditions">A JSON array of conditions combined with AND; omitted means none.</param>
    /// <param name="ValidFrom">When the rule starts; the time of the request when omitted.</param>
    internal sealed record DiscountRuleRequest(
        string? Name,
        string? Type,
        JsonElement? Parameters,
        JsonElement? Conditions,
        int Priority,
        bool Stackable,
        bool OncePerClient,
        DateTimeOffset? ValidFrom,
        DateTimeOffset? ValidTo,
        bool IsActive = true);

    internal sealed record PreviewRequest(
        Guid? ClientId, string? Currency, List<OrderEndpoints.CreateOrderLine>? Items, DiscountRuleRequest? Rule, Guid? RuleId);

    private static async Task<Results<Ok<IReadOnlyList<DiscountRuleDto>>, ProblemHttpResult>> ListRules(
        ListDiscountRulesHandler handler, CancellationToken cancellationToken, bool? active = null)
    {
        var result = await handler.HandleAsync(new ListDiscountRulesQuery(active), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : OrderEndpoints.ToProblem(result.Error!);
    }

    private static async Task<Results<Ok<DiscountRuleDto>, ProblemHttpResult>> GetRule(
        Guid id, GetDiscountRuleHandler handler, HttpResponse response, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetDiscountRuleQuery(id), cancellationToken);

        return Respond(result, response);
    }

    private static async Task<Results<Created<DiscountRuleDto>, ProblemHttpResult>> CreateRule(
        DiscountRuleRequest request, ClaimsPrincipal user, CreateDiscountRuleHandler handler, HttpResponse response, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new CreateDiscountRuleCommand(request.ToDefinition(), OrdersAuthentication.SubjectOf(user) ?? string.Empty), cancellationToken);

        if (!result.IsSuccess)
        {
            return OrderEndpoints.ToProblem(result.Error!);
        }

        response.Headers.ETag = EntityTag.For(result.Value.Version);

        return TypedResults.Created($"/api/v1/admin/discount-rules/{result.Value.Id}", result.Value);
    }

    private static async Task<Results<Ok<DiscountRuleDto>, ProblemHttpResult>> ReplaceRule(
        Guid id,
        DiscountRuleRequest request,
        [FromHeader(Name = _ifMatch)] string? ifMatch,
        ClaimsPrincipal user,
        ReplaceDiscountRuleHandler handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new ReplaceDiscountRuleCommand(id, ifMatch, request.ToDefinition(), OrdersAuthentication.SubjectOf(user) ?? string.Empty), cancellationToken);

        return Respond(result, response);
    }

    private static async Task<Results<Ok<DiscountRuleDto>, ProblemHttpResult>> DeactivateRule(
        Guid id,
        [FromHeader(Name = _ifMatch)] string? ifMatch,
        ClaimsPrincipal user,
        DeactivateDiscountRuleHandler handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new DeactivateDiscountRuleCommand(id, ifMatch, OrdersAuthentication.SubjectOf(user) ?? string.Empty), cancellationToken);

        return Respond(result, response);
    }

    private static async Task<Results<Ok<PreviewDto>, ProblemHttpResult>> PreviewRule(
        PreviewRequest request, PreviewDiscountRuleHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.ToQuery(), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : OrderEndpoints.ToProblem(result.Error!);
    }

    private static Results<Ok<DiscountRuleDto>, ProblemHttpResult> Respond(Result<DiscountRuleDto> result, HttpResponse response)
    {
        if (!result.IsSuccess)
        {
            return OrderEndpoints.ToProblem(result.Error!);
        }

        response.Headers.ETag = EntityTag.For(result.Value.Version);

        return TypedResults.Ok(result.Value);
    }
}
