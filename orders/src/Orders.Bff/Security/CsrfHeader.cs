namespace Orders.Bff.Security;

/// <summary>
/// Unsafe requests to <c>/bff/*</c> must carry <c>X-CSRF</c>: a cross-site form or image cannot add a custom header, and
/// a cross-site script that could would need CORS, which the BFF never grants. The sign-in form is the one exception; it
/// is protected by its antiforgery token instead.
/// </summary>
internal static class CsrfHeader
{
    public const string HeaderName = "X-CSRF";

    public static IApplicationBuilder UseBffCsrfHeader(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var request = context.Request;
        var unsafeMethod = !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method));

        if (unsafeMethod
            && request.Path.StartsWithSegments("/bff")
            && !request.Path.Equals("/bff/login", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(request.Headers[HeaderName]))
        {
            await Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Missing X-CSRF header",
                detail: $"Requests that change data must send the {HeaderName} header.").ExecuteAsync(context);

            return;
        }

        await next(context);
    });
}
