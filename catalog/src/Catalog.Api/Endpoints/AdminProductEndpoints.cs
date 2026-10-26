using System.Security.Claims;
using Catalog.Api.Security;
using Catalog.Application;
using Catalog.Application.Products;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Catalog.Api.Endpoints;

internal static class AdminProductEndpoints
{
    public static IEndpointRouteBuilder MapAdminProductEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin/products")
            .WithTags("Admin products")
            .RequireAuthorization(AdminAuthorization.AdminPolicy);

        admin.MapPut("/{sku}", UpsertProduct)
            .WithName("UpsertProduct")
            .WithSummary("Create a product (201) or replace it (200, If-Match required: 412 on a stale version, 428 when missing).");

        admin.MapPatch("/{sku}/price", ChangePrice)
            .WithName("ChangeProductPrice")
            .WithSummary("Change one price (If-Match required: 412 on a stale version, 428 when missing) and record the price history.");

        admin.MapPost("/{sku}/deactivate", DeactivateProduct)
            .WithName("DeactivateProduct")
            .WithSummary("Take a product off sale (If-Match optional; when sent, 412 on a stale version).");

        return app;
    }

    internal sealed record UpsertProductRequest(
        string? Name,
        string? Description,
        string? Category,
        Dictionary<string, string>? Attributes,
        List<PriceRequest>? Prices);

    internal sealed record PriceRequest(string? Currency, decimal? Amount);

    internal sealed record ChangePriceRequest(string? Currency, decimal? Amount);

    private static async Task<Results<Ok<ProductDetailDto>, Created<ProductDetailDto>, ProblemHttpResult>> UpsertProduct(
        string sku,
        UpsertProductRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        ClaimsPrincipal user,
        UpsertProductHandler handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.ToCommand(sku, ifMatch, UserId(user)), cancellationToken);

        if (!result.IsSuccess)
        {
            return ProductEndpoints.ToProblem(result.Error!);
        }

        var outcome = result.Value;
        response.Headers.ETag = $"\"{outcome.Product.Version}\"";

        return outcome.Created
            ? TypedResults.Created($"/api/v1/products/{outcome.Product.Sku}", outcome.Product)
            : TypedResults.Ok(outcome.Product);
    }

    private static async Task<Results<Ok<ProductDetailDto>, ProblemHttpResult>> ChangePrice(
        string sku,
        ChangePriceRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        ClaimsPrincipal user,
        ChangePriceHandler handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.ToCommand(sku, ifMatch, UserId(user)), cancellationToken);

        return Respond(result, response);
    }

    private static async Task<Results<Ok<ProductDetailDto>, ProblemHttpResult>> DeactivateProduct(
        string sku,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        ClaimsPrincipal user,
        DeactivateProductHandler handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new DeactivateProductCommand(sku, ifMatch, UserId(user)), cancellationToken);

        return Respond(result, response);
    }

    private static Results<Ok<ProductDetailDto>, ProblemHttpResult> Respond(
        Result<ProductDetailDto> result, HttpResponse response)
    {
        if (!result.IsSuccess)
        {
            return ProductEndpoints.ToProblem(result.Error!);
        }

        response.Headers.ETag = $"\"{result.Value.Version}\"";

        return TypedResults.Ok(result.Value);
    }

    private static string UserId(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name ?? "unknown";
}
