using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Orders.Api.Security;

internal static class OrdersAuthentication
{
    public const string AdminPolicy = "Admin";
    public const string AdminRole = "Admin";

    /// <summary>
    /// Order routes need a signed-in user, admin routes the Admin role. Until the identity provider arrives (C40-C41)
    /// the only registered scheme authenticates nobody, so these routes answer 401 outside tests, which register
    /// their own scheme.
    /// </summary>
    public static IServiceCollection AddOrdersAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(UnconfiguredAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, UnconfiguredAuthenticationHandler>(
                UnconfiguredAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.RequireRole(AdminRole));

        return services;
    }

    public static string? SubjectOf(ClaimsPrincipal user) => user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    public static string NameOf(ClaimsPrincipal user) =>
        user.FindFirstValue("name") ?? user.FindFirstValue("preferred_username") ?? SubjectOf(user) ?? string.Empty;
}

internal sealed class UnconfiguredAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Unconfigured";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}
