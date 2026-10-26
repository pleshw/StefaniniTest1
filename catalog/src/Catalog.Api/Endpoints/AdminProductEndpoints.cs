using System.Security.Claims;
using Catalog.Api.Security;
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

        return app;
    }

    internal sealed record UpsertProductRequest(
        string? Name,
        string? Description,
        string? Category,
        Dictionary<string, string>? Attributes,
        List<PriceRequest>? Prices);

    internal sealed record PriceRequest(string? Currency, decimal? Amount);

    private static async Task<Results<Ok<ProductDetailDto>, Created<ProductDetailDto>, ProblemHttpResult>> UpsertProduct(
        string sku,
        UpsertProductRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        ClaimsPrincipal user,
        UpsertProductHandler handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var command = new UpsertProductCommand(
            sku,
            ifMatch,
            request.Name,
            request.Description,
            request.Category,
            request.Attributes,
            request.Prices?.Select(price => new PriceInput(price.Currency, price.Amount)).ToList(),
            UserId(user));

        var result = await handler.HandleAsync(command, cancellationToken);

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

    private static string UserId(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name ?? "unknown";
}
