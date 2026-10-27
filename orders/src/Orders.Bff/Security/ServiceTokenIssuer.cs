using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ServiceDefaults;

namespace Orders.Bff.Security;

/// <summary>
/// Signs the short-lived JWT that carries the signed-in user to Orders or Catalog (subject, name, roles; one audience
/// per service). Tokens are cached per user, roles and audience and renewed a minute before they expire.
/// </summary>
internal sealed class ServiceTokenIssuer(IOptions<ServiceTokenOptions> options, IMemoryCache cache, TimeProvider time)
{
    private static readonly TimeSpan _renewBefore = TimeSpan.FromMinutes(1);

    private readonly ServiceTokenOptions _options = options.Value;
    private readonly SigningCredentials? _credentials = ServiceTokens.SigningKey(options.Value) is { } key
        ? new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        : null;

    /// <summary>The token for <paramref name="user"/>, or null without a session or a configured key.</summary>
    public string? TokenFor(ClaimsPrincipal user, string audience)
    {
        if (_credentials is null || user.Identity?.IsAuthenticated != true || user.FindFirstValue(ServiceTokens.SubjectClaim) is not { } subject)
        {
            return null;
        }

        var roles = user.FindAll(ClaimTypes.Role).Select(role => role.Value).Order(StringComparer.Ordinal).ToArray();
        var name = user.FindFirstValue(ServiceTokens.NameClaim) ?? subject;

        return cache.GetOrCreate($"service-token:{audience}:{subject}:{name}:{string.Join(',', roles)}", entry =>
        {
            var now = time.GetUtcNow();
            entry.AbsoluteExpiration = now + (_options.Lifetime > 2 * _renewBefore ? _options.Lifetime - _renewBefore : _options.Lifetime / 2);

            return Create(subject, name, roles, audience, now.UtcDateTime);
        });
    }

    private string Create(string subject, string name, string[] roles, string audience, DateTime now)
    {
        var claims = new Dictionary<string, object>
        {
            [ServiceTokens.SubjectClaim] = subject,
            [ServiceTokens.NameClaim] = name,
            [ServiceTokens.RoleClaim] = roles,
        };

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + _options.Lifetime,
            SigningCredentials = _credentials,
        });
    }
}

/// <summary>Adds the signed-in user's token to calls made by a typed client (composed routes).</summary>
internal sealed class DownstreamTokenHandler(IHttpContextAccessor accessor, ServiceTokenIssuer issuer, string audience) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (accessor.HttpContext?.User is { } user && issuer.TokenFor(user, audience) is { } token)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
