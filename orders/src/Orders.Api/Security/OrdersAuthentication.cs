using System.Security.Claims;
using ServiceDefaults;

namespace Orders.Api.Security;

internal static class OrdersAuthentication
{
    public const string AdminPolicy = "Admin";
    public const string AdminRole = "Admin";

    /// <summary>
    /// Order routes need a signed-in user, admin routes the Admin role. The user arrives as the JWT the BFF signs for
    /// the audience <c>orders-api</c> (<see cref="ServiceTokens"/>); without a configured key no token is accepted.
    /// Tests replace the scheme with their own.
    /// </summary>
    public static IServiceCollection AddOrdersAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddServiceTokenAuthentication(configuration, ServiceTokens.OrdersAudience);
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.RequireRole(AdminRole));

        return services;
    }

    public static string? SubjectOf(ClaimsPrincipal user) => user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    public static string NameOf(ClaimsPrincipal user) =>
        user.FindFirstValue("name") ?? user.FindFirstValue("preferred_username") ?? SubjectOf(user) ?? string.Empty;
}
