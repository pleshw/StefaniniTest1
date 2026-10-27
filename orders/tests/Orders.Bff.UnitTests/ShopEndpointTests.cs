using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Orders.Bff.UnitTests.Support;

namespace Orders.Bff.UnitTests;

/// <summary>The Shop routes: Catalog products as shop view models, and the shopper's quote and orders through Orders.</summary>
public sealed class ShopEndpointTests : IAsyncDisposable
{
    private const string _shopper = "5b0a3c1e-7d42-4f6a-9c1b-2e8f0d6a4b11";
    private const string _orderId = "6f1c2d3e-0000-4000-8000-000000000001";
    private const string _problem = """{"type":"discount-already-used","title":"Conflict","status":409,"detail":"kept as is"}""";

    private const string _productPage = """
        {
          "items": [
            {
              "sku": "KB-1", "name": "Keyboard", "description": "Mechanical", "category": "peripherals",
              "attributes": { "layout": "ABNT2" }, "price": { "amount": 199.90, "currency": "BRL" }, "isActive": true, "version": 3
            }
          ],
          "next": "cursor-2"
        }
        """;

    private readonly BffFactory _factory = new();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Products_MapsCatalogProductsToTheShopViewModel()
    {
        Respond(HttpStatusCode.OK, _productPage);

        var page = await _factory.CreateBrowser().GetFromJsonAsync<JsonElement>("/bff/shop/products?q=key&category=peripherals&after=c1&limit=10", Token);

        var forwarded = _factory.Downstream.Requests.ShouldHaveSingleItem();
        forwarded.Uri.GetLeftPart(UriPartial.Path).ShouldBe(BffFactory.CatalogAddress + "/api/v1/products");
        Query(forwarded).ShouldBe(new Dictionary<string, string> { ["q"] = "key", ["category"] = "peripherals", ["currency"] = "BRL", ["after"] = "c1", ["limit"] = "10" });
        page.GetProperty("next").GetString().ShouldBe("cursor-2");
        var product = page.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        product.EnumerateObject().Select(property => property.Name).ShouldBe(["sku", "name", "description", "category", "price"]);
        product.GetProperty("price").GetProperty("amount").GetDecimal().ShouldBe(199.90m);
        product.GetProperty("price").GetProperty("currency").GetString().ShouldBe("BRL");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Products_CurrencyChosenByTheShopper_IsUsedInsteadOfBrl()
    {
        Respond(HttpStatusCode.OK, """{ "items": [], "next": null }""");

        await _factory.CreateBrowser().GetAsync("/bff/shop/products?currency=USD", Token);

        Query(_factory.Downstream.Requests.ShouldHaveSingleItem())["currency"].ShouldBe("USD");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Products_CatalogUnreachable_Returns503ProblemDetails()
    {
        _factory.Downstream.Respond = _ => throw new HttpRequestException("connection refused");

        var response = await _factory.CreateBrowser().GetAsync("/bff/shop/products", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("status").GetInt32().ShouldBe(503);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Quote_SendsTheCartWithBrlByDefaultAndReturnsTheQuote()
    {
        Respond(HttpStatusCode.OK, """{"currency":"BRL","items":[],"itemCount":2,"subtotal":200.00,"discountTotal":20.00,"total":180.00,"discounts":[]}""");
        var browser = await SignedIn();

        var response = await browser.PostAsync("/bff/shop/cart/quote", Json("""{"items":[{"sku":"KB-1","quantity":2}]}"""), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("total").GetDecimal().ShouldBe(180.00m);
        var forwarded = _factory.Downstream.Requests.ShouldHaveSingleItem();
        (forwarded.Method, forwarded.Uri.ToString()).ShouldBe((HttpMethod.Post, BffFactory.OrdersAddress + "/api/v1/orders/quote"));
        var body = JsonDocument.Parse(forwarded.Body!).RootElement;
        body.GetProperty("currency").GetString().ShouldBe("BRL");
        body.GetProperty("items")[0].GetProperty("quantity").GetInt32().ShouldBe(2);
        forwarded.Headers["Authorization"].ShouldStartWith("Bearer ");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PlaceOrder_ForwardsBodyAndIdempotencyKeyUnchangedAndReturnsTheCreatedOrder()
    {
        _factory.Downstream.Respond = _ =>
        {
            var created = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent($$"""{"id":"{{_orderId}}"}""", Encoding.UTF8, "application/json") };
            created.Headers.Location = new Uri($"/api/v1/orders/{_orderId}", UriKind.Relative);

            return created;
        };
        var browser = await SignedIn();
        const string cart = """{"currency":"USD","items":[{"sku":"KB-1","quantity":1}]}""";

        var response = await browser.SendAsync(PlaceOrder(cart, "7d1c2d3e-0000-4000-8000-0000000000aa"), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.OriginalString.ShouldBe($"/bff/shop/orders/{_orderId}");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetString().ShouldBe(_orderId);
        var forwarded = _factory.Downstream.Requests.ShouldHaveSingleItem();
        (forwarded.Method, forwarded.Uri.ToString()).ShouldBe((HttpMethod.Post, BffFactory.OrdersAddress + "/api/v1/orders"));
        forwarded.Headers["Idempotency-Key"].ShouldBe("7d1c2d3e-0000-4000-8000-0000000000aa");
        forwarded.Body.ShouldBe(cart);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.BadRequest)]
    [Trait("Category", "Unit")]
    public async Task PlaceOrder_OrdersRefuses_PassesTheProblemThroughUnchanged(HttpStatusCode status)
    {
        Respond(status, _problem, "application/problem+json");
        var browser = await SignedIn();

        var response = await browser.SendAsync(PlaceOrder("""{"items":[]}""", Guid.NewGuid().ToString()), Token);

        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadAsStringAsync(Token)).ShouldBe(_problem);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Quote_OrdersRefuses_PassesTheProblemThroughUnchanged()
    {
        Respond(HttpStatusCode.UnprocessableEntity, _problem, "application/problem+json");
        var browser = await SignedIn();

        var response = await browser.PostAsync("/bff/shop/cart/quote", Json("""{"items":[{"sku":"NOPE","quantity":1}]}"""), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(Token)).ShouldBe(_problem);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MyOrders_AreScopedToTheSignedInClient()
    {
        Respond(HttpStatusCode.OK, """{"items":[],"next":null}""");
        var browser = await SignedIn();

        var response = await browser.GetAsync("/bff/shop/orders?after=c1&limit=20", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var forwarded = _factory.Downstream.Requests.ShouldHaveSingleItem();
        forwarded.Uri.GetLeftPart(UriPartial.Path).ShouldBe(BffFactory.OrdersAddress + "/api/v1/orders");
        Query(forwarded).ShouldBe(new Dictionary<string, string> { ["clientId"] = _shopper, ["after"] = "c1", ["limit"] = "20" });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task OrderDetail_NotFoundInOrders_Returns404Unchanged()
    {
        Respond(HttpStatusCode.NotFound, """{"type":"not-found","title":"Not Found","status":404}""", "application/problem+json");
        var browser = await SignedIn();

        var response = await browser.GetAsync($"/bff/shop/orders/{_orderId}", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _factory.Downstream.Requests.ShouldHaveSingleItem().Uri.ToString().ShouldBe(BffFactory.OrdersAddress + $"/api/v1/orders/{_orderId}");
    }

    [Theory]
    [InlineData("POST", "/bff/shop/cart/quote")]
    [InlineData("POST", "/bff/shop/orders")]
    [InlineData("GET", "/bff/shop/orders")]
    [InlineData("GET", $"/bff/shop/orders/{_orderId}")]
    [Trait("Category", "Unit")]
    public async Task OrderRoutes_Anonymous_Return401AndCallNothing(string method, string path)
    {
        var browser = _factory.CreateBrowser();
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "POST" ? Json("{}") : null };
        request.Headers.Add("X-CSRF", "1");

        var response = await browser.SendAsync(request, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _factory.Downstream.Requests.ShouldBeEmpty();
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private void Respond(HttpStatusCode status, string body, string mediaType = "application/json") =>
        _factory.Downstream.Respond = _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private async Task<HttpClient> SignedIn()
    {
        var browser = _factory.CreateBrowser();
        await BffFactory.SignInAsync(browser, "shopper", BffFactory.ShopperPassword);
        browser.DefaultRequestHeaders.Add("X-CSRF", "1");

        return browser;
    }

    private static HttpRequestMessage PlaceOrder(string body, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/bff/shop/orders") { Content = Json(body) };
        request.Headers.Add("Idempotency-Key", key);

        return request;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static Dictionary<string, string> Query(RecordedRequest request) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.Uri.Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString());

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
