using Catalog.Application;
using Catalog.Application.Products;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Catalog.Api.Endpoints;

internal static class ProductEndpoints
{
    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var products = app.MapGroup("/api/v1/products").WithTags("Products");

        products.MapGet("/", ListProducts)
            .WithName("ListProducts")
            .WithSummary("Search and page active products with their price in one currency.");

        products.MapGet("/{sku}", GetProduct)
            .WithName("GetProduct")
            .WithSummary("One product; the ETag is its version.");

        products.MapPost("/prices:query", QueryPrices)
            .WithName("QueryPrices")
            .WithSummary("Batch price lookup for 1-100 distinct SKUs in one currency; one MongoDB query.");

        return app;
    }

    internal sealed record PriceQueryRequest(string? Currency, string[]? Skus);

    private static async Task<Results<Ok<PriceQueryResult>, ProblemHttpResult>> QueryPrices(
        PriceQueryRequest request,
        QueryPricesHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new PriceQuery(request.Currency, request.Skus), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : ToProblem(result.Error!);
    }

    private static async Task<Results<Ok<ProductPage>, ProblemHttpResult>> ListProducts(
        [FromQuery] string? q,
        [FromQuery] string? category,
        [FromQuery] string? currency,
        [FromQuery] string? after,
        [FromQuery] int? limit,
        ListProductsHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListProductsQuery(q, category, currency, after, limit), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : ToProblem(result.Error!);
    }

    private static async Task<Results<Ok<ProductDto>, ProblemHttpResult>> GetProduct(
        string sku,
        [FromQuery] string? currency,
        GetProductHandler handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetProductQuery(sku, currency), cancellationToken);

        if (!result.IsSuccess)
        {
            return ToProblem(result.Error!);
        }

        response.Headers.ETag = $"\"{result.Value.Version}\"";
        return TypedResults.Ok(result.Value);
    }

    internal static ProblemHttpResult ToProblem(ResultError error) => error.Kind switch
    {
        ErrorKind.NotFound => TypedResults.Problem(detail: error.Message, statusCode: StatusCodes.Status404NotFound, title: "Not found"),
        ErrorKind.PreconditionFailed => TypedResults.Problem(detail: error.Message, statusCode: StatusCodes.Status412PreconditionFailed, title: "Precondition failed"),
        ErrorKind.PreconditionRequired => TypedResults.Problem(detail: error.Message, statusCode: StatusCodes.Status428PreconditionRequired, title: "Precondition required"),
        _ => TypedResults.Problem(detail: error.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request"),
    };
}
