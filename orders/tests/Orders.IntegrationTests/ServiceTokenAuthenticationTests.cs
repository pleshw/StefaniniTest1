using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ServiceDefaults;

namespace Orders.IntegrationTests;

/// <summary>
/// The real authentication of Orders.Api: a JWT the BFF signs for the audience <c>orders-api</c>, validated for issuer,
/// audience, lifetime and signature; roles come from the token.
/// </summary>
public sealed class ServiceTokenAuthenticationTests : IAsyncDisposable
{
    private static readonly Guid _shopper = Guid.Parse("5b0a3c1e-7d42-4f6a-9c1b-2e8f0d6a4b11");

    private readonly OrdersApiFactory _factory = new() { UseTestAuthentication = false };
    private readonly HttpClient _client;

    public ServiceTokenAuthenticationTests()
    {
        _client = _factory.CreateClient();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ShopperToken_OnAnAdminRoute_Returns403()
    {
        var response = await Get("/api/v1/admin/events", Token(roles: ["Shopper"]));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AdminToken_OnAnAdminRoute_IsAuthorized()
    {
        var response = await Get("/api/v1/admin/events", Token(roles: ["Admin"]));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ShopperToken_ListsTheOrdersOfTheClientNamedBySub()
    {
        var response = await Get("/api/v1/orders", Token(roles: ["Shopper"]));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
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
            "wrong-audience" => Token(roles: ["Admin"], audience: ServiceTokens.CatalogAudience),
            "wrong-issuer" => Token(roles: ["Admin"], issuer: "someone-else"),
            "expired" => Token(roles: ["Admin"], expires: DateTime.UtcNow.AddMinutes(-10)),
            _ => Token(roles: ["Admin"], key: "another-signing-key-that-is-long-enough-0123456789"),
        };

        var response = await Get("/api/v1/admin/events", token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TestSchemeHeaders_AreIgnoredByTheRealScheme()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/events");
        request.Headers.Add(TestAuthHandler.UserHeader, "admin-1");
        request.Headers.Add(TestAuthHandler.RolesHeader, "Admin");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private Task<HttpResponseMessage> Get(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (token is not null)
        {
            request.Headers.Authorization = new("Bearer", token);
        }

        return _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string Token(
        string[] roles,
        string audience = ServiceTokens.OrdersAudience,
        string issuer = ServiceTokens.DefaultIssuer,
        DateTime? expires = null,
        string key = OrdersApiFactory.ServiceTokenSigningKey)
    {
        var claims = new List<Claim> { new("sub", _shopper.ToString()), new("name", "shopper") };
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
