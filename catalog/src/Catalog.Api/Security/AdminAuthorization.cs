using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Catalog.Api.Security;

internal static class AdminAuthorization
{
    public const string AdminPolicy = "Admin";
    public const string AdminRole = "Admin";

    /// <summary>
    /// Admin routes require the Admin role. Until the identity provider arrives (Phase 5) the only registered scheme
    /// authenticates nobody, so these routes answer 401 outside tests, which register their own scheme.
    /// </summary>
    public static IServiceCollection AddAdminAuthorization(this IServiceCollection services)
    {
        services.AddAuthentication(UnconfiguredAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, UnconfiguredAuthenticationHandler>(
                UnconfiguredAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.RequireRole(AdminRole));

        return services;
    }
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
