using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog.Application.Products;
using Catalog.Domain;

namespace Catalog.IntegrationTests;

public sealed class AdminPriceEndpointTests : IClassFixture<CatalogApiFactory>
{
    private readonly CatalogApiFactory _factory;
    private readonly HttpClient _client;

    public AdminPriceEndpointTests(CatalogApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PatchPrice_MatchingIfMatch_ReturnsTheUpdatedProductWithTheNewEtagAndHistory()
    {
        Seed("PRICE-1");

        var response = await Send(HttpMethod.Patch, "/api/v1/admin/products/price-1/price", new { currency = "BRL", amount = 150m }, ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.ShouldBe("\"2\"");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        body.GetProperty("prices")[0].GetProperty("amount").GetDecimal().ShouldBe(150m);
        _factory.Products.PriceHistory.ShouldContain(entry => entry.Sku.Value == "PRICE-1" && entry.OldPrice == 100m && entry.NewPrice == 150m);
        _factory.Products.Audits["PRICE-1"].UpdatedBy.ShouldBe("admin-1");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PatchPrice_StaleIfMatch_Returns412()
    {
        Seed("PRICE-2");

        var response = await Send(HttpMethod.Patch, "/api/v1/admin/products/price-2/price", new { currency = "BRL", amount = 150m }, ifMatch: "\"7\"");

        await AssertProblem(response, HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PatchPrice_NoIfMatch_Returns428()
    {
        Seed("PRICE-3");

        var response = await Send(HttpMethod.Patch, "/api/v1/admin/products/price-3/price", new { currency = "BRL", amount = 150m });

        await AssertProblem(response, HttpStatusCode.PreconditionRequired);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PatchPrice_UnknownSkuOrNegativeAmount_Returns404Or400()
    {
        Seed("PRICE-4");

        var unknown = await Send(HttpMethod.Patch, "/api/v1/admin/products/nope/price", new { currency = "BRL", amount = 1m }, ifMatch: "\"1\"");
        var negative = await Send(HttpMethod.Patch, "/api/v1/admin/products/price-4/price", new { currency = "BRL", amount = -1m }, ifMatch: "\"1\"");

        await AssertProblem(unknown, HttpStatusCode.NotFound);
        await AssertProblem(negative, HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PatchPrice_NonAdmin_Returns403()
    {
        Seed("PRICE-5");

        var response = await Send(HttpMethod.Patch, "/api/v1/admin/products/price-5/price", new { currency = "BRL", amount = 1m }, ifMatch: "\"1\"", admin: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Deactivate_ExistingProduct_ReturnsItInactiveWithANewEtag()
    {
        Seed("OFF-1");

        var response = await Send(HttpMethod.Post, "/api/v1/admin/products/off-1/deactivate", null, ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.ShouldBe("\"2\"");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        body.GetProperty("isActive").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Deactivate_StaleIfMatchOrUnknownSku_Returns412Or404()
    {
        Seed("OFF-2");

        var stale = await Send(HttpMethod.Post, "/api/v1/admin/products/off-2/deactivate", null, ifMatch: "\"9\"");
        var unknown = await Send(HttpMethod.Post, "/api/v1/admin/products/nope/deactivate", null);

        await AssertProblem(stale, HttpStatusCode.PreconditionFailed);
        await AssertProblem(unknown, HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Deactivate_NonAdminOrAnonymous_Returns403Or401()
    {
        Seed("OFF-3");

        var forbidden = await Send(HttpMethod.Post, "/api/v1/admin/products/off-3/deactivate", null, admin: false);
        var anonymous = await _client.PostAsync("/api/v1/admin/products/off-3/deactivate", null, Token);

        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private void Seed(string sku) => _factory.Products.Add(Product.Create(
        Sku.Create(sku),
        "Keyboard",
        "A keyboard",
        "peripherals",
        null,
        [Money.Create(100m, Currency.Create("BRL"))]));

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body, string? ifMatch = null, bool admin = true)
    {
        using var request = new HttpRequestMessage(method, url);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add(TestAuthHandler.UserHeader, "admin-1");
        request.Headers.Add(TestAuthHandler.RolesHeader, admin ? "Admin" : "Shopper");

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await _client.SendAsync(request, Token);
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        problem.GetProperty("status").GetInt32().ShouldBe((int)status);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
