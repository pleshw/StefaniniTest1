using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ServiceDefaults;

namespace Catalog.IntegrationTests;

/// <summary>
/// The real authentication of Catalog.Api: a JWT the BFF signs for the audience <c>catalog-api</c>, validated for issuer,
/// audience, lifetime and signature; roles come from the token.
/// </summary>
public sealed class ServiceTokenAuthenticationTests : IAsyncDisposable
{
    private const string _adminPath = "/api/v1/admin/products/NOPE-1/deactivate";

    private readonly CatalogApiFactory _factory = new() { UseTestAuthentication = false };
    private readonly HttpClient _client;

    public ServiceTokenAuthenticationTests()
    {
        _client = _factory.CreateClient();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ShopperToken_OnAnAdminRoute_Returns403()
    {
        var response = await Post(_adminPath, Token(roles: ["Shopper"]));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AdminToken_OnAnAdminRoute_IsAuthorized()
    {
        var response = await Post(_adminPath, Token(roles: ["Admin"]));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    public static TheoryData<string> RejectedTokens => new(["none", "wrong-audience", "wrong-issuer", "expired", "wrong-key"]);

    [Theory]
    [MemberData(nameof(RejectedTokens))]
    [Trait("Category", "Integration")]
    public async Task InvalidOrMissingToken_Returns401(string kind)
    {
        var token = kind switch
        {
            "none" => null,
            "wrong-audience" => Token(roles: ["Admin"], audience: ServiceTokens.OrdersAudience),
            "wrong-issuer" => Token(roles: ["Admin"], issuer: "someone-else"),
            "expired" => Token(roles: ["Admin"], expires: DateTime.UtcNow.AddMinutes(-10)),
            _ => Token(roles: ["Admin"], key: "another-signing-key-that-is-long-enough-0123456789"),
        };

        var response = await Post(_adminPath, token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProductQueries_StayOpenToInternalCallersWithoutAToken()
    {
        var response = await _client.GetAsync("/api/v1/products?currency=BRL", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private Task<HttpResponseMessage> Post(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (token is not null)
        {
            request.Headers.Authorization = new("Bearer", token);
        }

        return _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string Token(
        string[] roles,
        string audience = ServiceTokens.CatalogAudience,
        string issuer = ServiceTokens.DefaultIssuer,
        DateTime? expires = null,
        string key = CatalogApiFactory.ServiceTokenSigningKey)
    {
        var claims = new List<Claim> { new("sub", "a1d3e5f7-2b4c-4d6e-8f01-3c5e7a9b0d22"), new("name", "admin") };
        claims.AddRange(roles.Select(role => new Claim("role", role)));
        var notBefore = (expires ?? DateTime.UtcNow.AddMinutes(5)).AddMinutes(-15);

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = notBefore,
            IssuedAt = notBefore,
            Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256),
        });
    }
}
