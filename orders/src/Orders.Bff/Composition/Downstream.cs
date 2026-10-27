using Polly.CircuitBreaker;
using Polly.Timeout;
using CatalogApiException = Orders.Bff.Clients.Catalog.ApiException;
using OrdersApiException = Orders.Bff.Clients.Orders.ApiException;

namespace Orders.Bff.Composition;

/// <summary>
/// Calls a service through its typed client. A service's own error answer (ProblemDetails or not) goes back to the
/// browser unchanged; a service that cannot be reached (connection refused, timeout, open circuit) becomes a 503
/// ProblemDetails naming it.
/// </summary>
internal static class Downstream
{
    public static async Task<IResult> CallAsync<T>(string service, Func<Task<T>> call, Func<T, IResult> ok, CancellationToken cancellationToken)
    {
        try
        {
            return ok(await call());
        }
        catch (OrdersApiException error)
        {
            return new PassthroughResult(error.StatusCode, error.Response, ContentType(error.Headers));
        }
        catch (CatalogApiException error)
        {
            return new PassthroughResult(error.StatusCode, error.Response, ContentType(error.Headers));
        }
        catch (Exception error) when (IsUnavailable(error, cancellationToken))
        {
            return Unavailable(service);
        }
    }

    public static bool IsUnavailable(Exception error, CancellationToken cancellationToken) =>
        error is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
        || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    public static IResult Unavailable(string service) => TypedResults.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: $"{service} unavailable",
        detail: $"{service} could not be reached; try again shortly.");

    private static string? ContentType(IReadOnlyDictionary<string, IEnumerable<string>>? headers) =>
        headers?.FirstOrDefault(header => header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();
}

/// <summary>Writes a service's answer as it came: status, content type and body.</summary>
internal sealed class PassthroughResult(int status, string? body, string? contentType, string? location = null) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = status;

        if (location is not null)
        {
            httpContext.Response.Headers.Location = location;
        }

        if (!string.IsNullOrEmpty(body))
        {
            httpContext.Response.ContentType = contentType ?? "application/json";
            await httpContext.Response.WriteAsync(body, httpContext.RequestAborted);
        }
    }
}
