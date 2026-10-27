using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace ServiceDefaults;

/// <summary>
/// The JWT the BFF signs for each call to a service, on behalf of the signed-in user. Placeholder for an identity
/// provider's token exchange: one shared HMAC key (from <c>.env</c>) instead of the provider's signing keys.
/// </summary>
public sealed record ServiceTokenOptions
{
    public const string SectionName = "ServiceTokens";

    public string Issuer { get; init; } = ServiceTokens.DefaultIssuer;

    /// <summary>HMAC-SHA256 key, at least 32 bytes. Empty: the BFF sends no token and the services accept none (deny by default).</summary>
    public string SigningKey { get; init; } = string.Empty;

    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(5);
}

public static class ServiceTokens
{
    public const string DefaultIssuer = "orders-bff";
    public const string OrdersAudience = "orders-api";
    public const string CatalogAudience = "catalog-api";
    public const string SubjectClaim = "sub";
    public const string NameClaim = "name";
    public const string RoleClaim = "role";

    private const int _minimumKeyBytes = 32;

    public static ServiceTokenOptions ServiceTokenOptions(this IConfiguration configuration) =>
        configuration.GetSection(ServiceDefaults.ServiceTokenOptions.SectionName).Get<ServiceTokenOptions>() ?? new ServiceTokenOptions();

    /// <summary>The signing key, or null when none is configured. A key shorter than 32 bytes stops the host at startup.</summary>
    public static SymmetricSecurityKey? SigningKey(ServiceTokenOptions options)
    {
        if (string.IsNullOrEmpty(options.SigningKey))
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(options.SigningKey);

        return bytes.Length >= _minimumKeyBytes
            ? new SymmetricSecurityKey(bytes)
            : throw new InvalidOperationException($"ServiceTokens:SigningKey must have at least {_minimumKeyBytes} bytes.");
    }

    /// <summary>Issuer, audience, lifetime and signature are all checked; claims keep their JWT names (sub, name, role).</summary>
    public static TokenValidationParameters ValidationParameters(ServiceTokenOptions options, string audience)
    {
        var key = SigningKey(options);

        return new TokenValidationParameters
        {
            ValidIssuer = options.Issuer,
            ValidAudience = audience,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            IssuerSigningKeys = key is null ? [] : [key],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = NameClaim,
            RoleClaimType = RoleClaim,
        };
    }

    /// <summary>
    /// Makes the BFF's token the only way in: the JWT bearer scheme is the default, for <paramref name="audience"/>.
    /// Without a configured key every token fails, so protected routes stay closed.
    /// </summary>
    public static AuthenticationBuilder AddServiceTokenAuthentication(this IServiceCollection services, IConfiguration configuration, string audience)
    {
        var options = configuration.ServiceTokenOptions();
        var parameters = ValidationParameters(options, audience);

        return services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = parameters;
            });
    }
}
