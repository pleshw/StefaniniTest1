using ServiceDefaults;

namespace Catalog.Api.Security;

internal static class AdminAuthorization
{
    public const string AdminPolicy = "Admin";
    public const string AdminRole = "Admin";

    /// <summary>
    /// Admin routes require the Admin role. The user arrives as the JWT the BFF signs for the audience
    /// <c>catalog-api</c> (<see cref="ServiceTokens"/>); without a configured key no token is accepted. Product queries
    /// stay open to internal callers. Tests replace the scheme with their own.
    /// </summary>
    public static IServiceCollection AddAdminAuthorization(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddServiceTokenAuthentication(configuration, ServiceTokens.CatalogAudience);
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.RequireRole(AdminRole));

        return services;
    }
}
