using Microsoft.AspNetCore.Http.Features;

namespace Orders.Bff.Routing;

/// <summary>
/// Caps request bodies at <see cref="BffOptions.MaxRequestBodySize"/>: a declared length above it is answered with a 413
/// ProblemDetails before anything is read or forwarded, and the server limit stops a chunked body that grows past it.
/// </summary>
internal static class RequestSizeLimit
{
    public static IApplicationBuilder UseBffRequestSizeLimit(this IApplicationBuilder app, long maxBytes) => app.Use(async (context, next) =>
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = maxBytes;
        }

        if (context.Request.ContentLength > maxBytes)
        {
            await Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "Request body too large",
                detail: $"The body may have at most {maxBytes} bytes.").ExecuteAsync(context);

            return;
        }

        await next(context);
    });
}
