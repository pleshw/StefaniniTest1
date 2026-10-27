using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Orders.Bff.Security;
using Orders.Bff.UnitTests.Support;
using ServiceDefaults;

namespace Orders.Bff.UnitTests;

/// <summary>
/// Calls from the BFF to the services carry a short-lived JWT for the signed-in user (placeholder for an identity
/// provider's token exchange): subject, name and roles kept, one audience per service.
/// </summary>
public sealed class ServiceTokenTests : IAsyncDisposable
{
    private readonly BffFactory _factory = new();

    [Theory]
    [InlineData("/bff/admin/dead-letters", ServiceTokens.OrdersAudience)]
    [InlineData("/bff/admin/discount-rules", ServiceTokens.OrdersAudience)]
    [InlineData("/bff/admin/products/KB-1/deactivate", ServiceTokens.CatalogAudience)]
    [InlineData("/bff/admin/products/KB-1", ServiceTokens.CatalogAudience)]
    [Trait("Category", "Unit")]
    public async Task ForwardedRequest_SignedIn_CarriesATokenForThatServiceWithTheUserAndRoles(string path, string audience)
    {
        var browser = await SignedInBrowser("admin", BffFactory.AdminPassword);

        await Send(browser, path);

        var token = await Validate(BearerOf(_factory.Downstream.Requests.ShouldHaveSingleItem()), audience);
        token.GetClaim("sub").Value.ShouldBe("a1d3e5f7-2b4c-4d6e-8f01-3c5e7a9b0d22");
        token.GetClaim("name").Value.ShouldBe("admin");
        token.Claims.Where(claim => claim.Type == "role").Select(claim => claim.Value).ShouldBe(["Admin"]);
        (token.ValidTo - token.IssuedAt).ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ForwardedRequest_BrowserSentAToken_CarriesTheBffTokenInstead()
    {
        var browser = await SignedInBrowser("admin", BffFactory.AdminPassword);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/bff/admin/dead-letters");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "forged");

        await browser.SendAsync(request, Token);

        var token = await Validate(BearerOf(_factory.Downstream.Requests.ShouldHaveSingleItem()), ServiceTokens.OrdersAudience);
        token.GetClaim("name").Value.ShouldBe("admin");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ForwardedRequests_SameUserAndService_ReuseTheCachedToken()
    {
        var browser = await SignedInBrowser("admin", BffFactory.AdminPassword);

        await Send(browser, "/bff/admin/dead-letters");
        await Send(browser, "/bff/admin/confirmation-rules");

        _factory.Downstream.Requests.Select(BearerOf).Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TypedClientHandler_SignedInUser_AddsTheTokenForItsAudience()
    {
        var issuer = Issuer();
        var context = new DefaultHttpContext { User = DevUser.Shopper.ToPrincipal() };
        var inner = new RecordingHandler();
        using var client = new HttpClient(new DownstreamTokenHandler(new HttpContextAccessor { HttpContext = context }, issuer, ServiceTokens.CatalogAudience) { InnerHandler = inner });

        await client.GetAsync("http://catalog.test/api/v1/products", Token);

        var token = await Validate(BearerOf(inner.Requests.ShouldHaveSingleItem()), ServiceTokens.CatalogAudience);
        token.Claims.Where(claim => claim.Type == "role").Select(claim => claim.Value).ShouldBe(["Shopper"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TypedClientHandler_NoUser_SendsNoToken()
    {
        var inner = new RecordingHandler();
        using var client = new HttpClient(new DownstreamTokenHandler(new HttpContextAccessor(), Issuer(), ServiceTokens.OrdersAudience) { InnerHandler = inner });

        await client.GetAsync("http://orders.test/api/v1/orders", Token);

        inner.Requests.ShouldHaveSingleItem().Headers.ContainsKey("Authorization").ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Issuer_SigningKeyShorterThan32Bytes_FailsFast()
    {
        Should.Throw<InvalidOperationException>(() => ServiceTokens.SigningKey(new ServiceTokenOptions { SigningKey = "too-short" }));
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static ServiceTokenIssuer Issuer() => new(
        Options.Create(new ServiceTokenOptions { SigningKey = BffFactory.SigningKey }), new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);

    private async Task<HttpClient> SignedInBrowser(string user, string password)
    {
        var browser = _factory.CreateBrowser();
        await BffFactory.SignInAsync(browser, user, password);
        browser.DefaultRequestHeaders.Add("X-CSRF", "1");

        return browser;
    }

    private static Task<HttpResponseMessage> Send(HttpClient browser, string path) =>
        path.EndsWith("/deactivate", StringComparison.Ordinal)
            ? browser.PostAsync(path, new StringContent("{}"), Token)
            : browser.GetAsync(path, Token);

    private static string BearerOf(RecordedRequest request)
    {
        var header = request.Headers["Authorization"];
        header.ShouldStartWith("Bearer ");

        return header["Bearer ".Length..];
    }

    private static async Task<JsonWebToken> Validate(string token, string audience)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(
            token, ServiceTokens.ValidationParameters(new ServiceTokenOptions { SigningKey = BffFactory.SigningKey }, audience));
        result.IsValid.ShouldBeTrue(result.Exception?.Message);

        return (JsonWebToken)result.SecurityToken;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
