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
        var part = await TryAsync(service, call, cancellationToken);

        return part.Failure ?? ok(part.Value!);
    }

    /// <summary>One part of a composed answer: the value, or the result the browser would get for the failure.</summary>
    public static async Task<Part<T>> TryAsync<T>(string service, Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return new Part<T>(await call(), null);
        }
        catch (OrdersApiException error)
        {
            return new Part<T>(default, new PassthroughResult(error.StatusCode, error.Response, ContentType(error.Headers)));
        }
        catch (CatalogApiException error)
        {
            return new Part<T>(default, new PassthroughResult(error.StatusCode, error.Response, ContentType(error.Headers)));
        }
        catch (Exception error) when (IsUnavailable(error, cancellationToken))
        {
            return new Part<T>(default, Unavailable(service));
        }
    }

    public static bool IsUnavailable(Exception error, CancellationToken cancellationToken) =>
        error is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
        || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    public static IResult Unavailable(string service) => TypedResults.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: $"{service} unavailable",
        detail: $"{service} could not be reached; try again shortly.");

    /// <summary>
    /// Forwards the request body and its <c>Idempotency-Key</c> unchanged to <paramref name="path"/> and returns the
    /// service's answer as it is (status, content type, body), with a <c>Location</c> under <paramref name="servicePrefix"/>
    /// moved under <paramref name="publicPrefix"/>. The body is buffered so a retry can send it again with the same key.
    /// </summary>
    public static async Task<IResult> ForwardAsync(
        HttpContext context, HttpClient client, string service, string path, string servicePrefix, string publicPrefix, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body.ToArray()) };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType ?? "application/json");

        if (context.Request.Headers.TryGetValue("Idempotency-Key", out var key))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString());
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var location = response.Headers.Location?.OriginalString is { } target && target.StartsWith(servicePrefix, StringComparison.Ordinal)
                ? publicPrefix + target[servicePrefix.Length..]
                : null;

            return new PassthroughResult(
                (int)response.StatusCode,
                await response.Content.ReadAsStringAsync(cancellationToken),
                response.Content.Headers.ContentType?.ToString(),
                location);
        }
        catch (Exception error) when (IsUnavailable(error, cancellationToken))
        {
            return Unavailable(service);
        }
    }

    private static string? ContentType(IReadOnlyDictionary<string, IEnumerable<string>>? headers) =>
        headers?.FirstOrDefault(header => header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();
}

internal sealed record Part<T>(T? Value, IResult? Failure);

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
