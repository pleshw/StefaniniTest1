using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orders.Bff.UnitTests.Support;

namespace Orders.Bff.UnitTests;

/// <summary>The development sign-in (placeholder for an identity provider) and the cookie session it opens.</summary>
public sealed class SessionTests : IAsyncDisposable
{
    private readonly BffFactory _factory = new();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task User_Anonymous_Returns401NotARedirect()
    {
        var browser = _factory.CreateBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/bff/user");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await browser.SendAsync(request, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Location.ShouldBeNull();
    }

    [Theory]
    [InlineData("shopper", BffFactory.ShopperPassword, new[] { "Shopper" })]
    [InlineData("admin", BffFactory.AdminPassword, new[] { "Admin" })]
    [Trait("Category", "Unit")]
    public async Task User_SignedIn_ReturnsSubjectNameAndRoles(string user, string password, string[] roles)
    {
        var browser = _factory.CreateBrowser();
        (await BffFactory.SignInAsync(browser, user, password)).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        var me = await browser.GetFromJsonAsync<JsonElement>("/bff/user", Token);

        Guid.TryParse(me.GetProperty("sub").GetString(), out _).ShouldBeTrue();
        me.GetProperty("name").GetString().ShouldBe(user);
        me.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).ShouldBe(roles);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_ValidPassword_SetsAnHttpOnlySecureStrictCookieAndRedirectsToTheLocalReturnUrl()
    {
        var browser = _factory.CreateBrowser();

        var response = await BffFactory.SignInAsync(browser, "shopper", BffFactory.ShopperPassword, "/shop.html");

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe("/shop.html");
        var cookie = response.Headers.GetValues("Set-Cookie").ShouldHaveSingleItem().ToLowerInvariant();
        cookie.ShouldStartWith(".orders.bff=");
        cookie.ShouldContain("httponly");
        cookie.ShouldContain("secure");
        cookie.ShouldContain("samesite=strict");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_OverPlainHttpBehindNginx_StillSetsASecureSessionCookie()
    {
        var browser = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await BffFactory.SignInAsync(browser, "admin", BffFactory.AdminPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.GetValues("Set-Cookie").ShouldContain(cookie => cookie.StartsWith(".orders.bff=", StringComparison.Ordinal) && cookie.Contains("secure"));
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    [InlineData("/\t/evil.example/")]
    [InlineData("/\n/evil.example/")]
    [Trait("Category", "Unit")]
    public async Task Login_ReturnUrlOffSite_RedirectsHome(string returnUrl)
    {
        var browser = _factory.CreateBrowser();

        var response = await BffFactory.SignInAsync(browser, "shopper", BffFactory.ShopperPassword, returnUrl);

        response.Headers.Location!.OriginalString.ShouldBe("/");
    }

    [Theory]
    [InlineData("shopper", "wrong")]
    [InlineData("shopper", BffFactory.AdminPassword)]
    [InlineData("nobody", BffFactory.ShopperPassword)]
    [InlineData("shopper", "")]
    [Trait("Category", "Unit")]
    public async Task Login_WrongCredentials_Returns401AndNoCookie(string user, string password)
    {
        var browser = _factory.CreateBrowser();

        var response = await BffFactory.SignInAsync(browser, user, password);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        (await browser.GetAsync("/bff/user", Token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_WithoutTheAntiforgeryToken_Returns400()
    {
        var browser = _factory.CreateBrowser();

        var response = await browser.PostAsync(
            "/bff/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "shopper", ["password"] = BffFactory.ShopperPassword }),
            Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_DevLoginDisabled_IsNotFound()
    {
        await using var factory = new BffFactory { DevLoginEnabled = false };
        var browser = factory.CreateBrowser();

        (await browser.GetAsync("/bff/login", Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Logout_WithTheCsrfHeader_EndsTheSession()
    {
        var browser = _factory.CreateBrowser();
        await BffFactory.SignInAsync(browser, "shopper", BffFactory.ShopperPassword);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        request.Headers.Add("X-CSRF", "1");

        var response = await browser.SendAsync(request, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await browser.GetAsync("/bff/user", Token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("POST", "/bff/logout")]
    [InlineData("POST", "/bff/admin/discount-rules")]
    [InlineData("PUT", "/bff/admin/products/KB-1")]
    [InlineData("PATCH", "/bff/admin/products/KB-1/price")]
    [InlineData("DELETE", "/bff/admin/discount-rules/1")]
    [Trait("Category", "Unit")]
    public async Task UnsafeMethod_WithoutTheCsrfHeader_Returns400AndForwardsNothing(string method, string path)
    {
        var browser = _factory.CreateBrowser();
        await BffFactory.SignInAsync(browser, "admin", BffFactory.AdminPassword);

        var response = await browser.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        _factory.Downstream.Requests.ShouldBeEmpty();
        (await browser.GetAsync("/bff/user", Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ForwardedRequest_DoesNotCarryTheBrowserCookies()
    {
        var browser = _factory.CreateBrowser();
        await BffFactory.SignInAsync(browser, "admin", BffFactory.AdminPassword);

        await browser.GetAsync("/bff/admin/dead-letters", Token);

        _factory.Downstream.Requests.ShouldHaveSingleItem().Headers.ContainsKey("Cookie").ShouldBeFalse();
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
