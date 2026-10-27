using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Orders.Bff.Security;

/// <summary>
/// The browser session: an encrypted cookie (HttpOnly, Secure, SameSite=Strict) that never leaves the BFF. Calls to
/// <c>/bff/*</c> without a session get 401 or 403, never a redirect, so the pages decide where to send the user.
/// </summary>
internal static class BffAuthentication
{
    public const string CookieScheme = "bff";
    public const string CookieName = ".orders.bff";
    public const string SubjectClaim = "sub";
    public const string NameClaim = "name";
    public const string ShopperRole = "Shopper";
    public const string AdminRole = "Admin";
    public const string AdminPolicy = "Admin";

    public static IServiceCollection AddBffAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(CookieScheme)
            .AddCookie(CookieScheme, cookie =>
            {
                cookie.Cookie.Name = CookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
                cookie.SlidingExpiration = true;
                cookie.Events.OnRedirectToLogin = context => Status(context, StatusCodes.Status401Unauthorized);
                cookie.Events.OnRedirectToAccessDenied = context => Status(context, StatusCodes.Status403Forbidden);
            });
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.RequireRole(AdminRole));
        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ServiceTokenIssuer>();
        // nginx talks plain http to the BFF, and antiforgery refuses a Secure-only cookie on such a request; the token
        // cookie only pairs with the form field, so it follows the request scheme. The session cookie stays Secure.
        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Cookie.Name = ".orders.bff.antiforgery";
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
        });

        return services;
    }

    /// <summary><c>/bff/user</c>, <c>/bff/logout</c> and, when the development sign-in is on, <c>/bff/login</c> and its files.</summary>
    public static IEndpointRouteBuilder MapBffSession(this IEndpointRouteBuilder endpoints, DevLoginOptions devLogin)
    {
        var session = endpoints.MapGroup("/bff").WithTags("Session");

        session.MapGet("/user", (ClaimsPrincipal user) => TypedResults.Ok(new BffUser(
                user.FindFirstValue(SubjectClaim) ?? string.Empty,
                user.FindFirstValue(NameClaim) ?? string.Empty,
                [.. user.FindAll(ClaimTypes.Role).Select(role => role.Value)])))
            .RequireAuthorization()
            .WithSummary("The signed-in user: subject, name and roles. 401 without a session.");

        session.MapPost("/logout", async (HttpContext context) =>
            {
                await context.SignOutAsync(CookieScheme);

                return TypedResults.NoContent();
            })
            .WithSummary("Ends the session. Requires the X-CSRF header.");

        if (devLogin.Enabled)
        {
            session.MapGet("/login", (HttpContext context, IAntiforgery antiforgery, string? returnUrl) => LoginForm(context, antiforgery, returnUrl, failed: false))
                .ExcludeFromDescription();
            session.MapGet("/login/{file}", (HttpContext context, string file) =>
                {
                    if (!LoginPage.Assets.TryGetValue(file, out var asset))
                    {
                        return Results.NotFound();
                    }

                    context.Response.Headers.CacheControl = "public, max-age=86400";

                    return Results.File(asset.Content, asset.ContentType);
                })
                .ExcludeFromDescription();
            // The token is checked in the handler, so a missing one is a 400 ProblemDetails rather than an exception.
            session.MapPost("/login", SignInAsync)
                .DisableAntiforgery()
                .ExcludeFromDescription();
        }

        return endpoints;
    }

    private static async Task<IResult> SignInAsync(HttpContext context, IAntiforgery antiforgery, IOptions<DevLoginOptions> devLogin, [FromForm] LoginForm form)
    {
        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid sign-in form",
                detail: "The form has expired or did not come from this site; reload the sign-in page.");
        }

        if (DevLoginUsers.Verify(devLogin.Value, form.UserName, form.Password) is not { } user)
        {
            return LoginForm(context, antiforgery, form.ReturnUrl, failed: true, form.UserName);
        }

        await context.SignInAsync(CookieScheme, user.ToPrincipal());

        // Without a return URL (the sign-in page opened directly) the user goes to their own page, not the index.
        return TypedResults.Redirect(IsLocal(form.ReturnUrl) ? form.ReturnUrl! : user.HomePage);
    }

    private static IResult LoginForm(HttpContext context, IAntiforgery antiforgery, string? returnUrl, bool failed, string? userName = null)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);

        return Results.Content(
            LoginPage.Render(tokens.FormFieldName, tokens.RequestToken!, returnUrl, failed, userName),
            "text/html; charset=utf-8",
            statusCode: failed ? StatusCodes.Status401Unauthorized : StatusCodes.Status200OK);
    }

    /// <summary>
    /// Only a path on this site: "/x" but not "//host" or "/\host", which browsers treat as another origin, nor a path with a
    /// control character, because browsers drop tabs and line breaks from a URL and "/&lt;tab&gt;/host" becomes "//host".
    /// </summary>
    private static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\')) && !url.Any(char.IsControl);

    private static Task Status(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        context.Response.StatusCode = status;

        return Task.CompletedTask;
    }
}

internal sealed record BffUser(string Sub, string Name, IReadOnlyList<string> Roles);

internal sealed record LoginForm
{
    [FromForm(Name = "username")]
    public string? UserName { get; init; }

    [FromForm(Name = "password")]
    public string? Password { get; init; }

    [FromForm(Name = "returnUrl")]
    public string? ReturnUrl { get; init; }
}
