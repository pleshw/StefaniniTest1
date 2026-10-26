using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Catalog.IntegrationTests;

public sealed class ProductQueryEndpointTests : IClassFixture<CatalogApiFactory>
{
    private readonly HttpClient _client;

    public ProductQueryEndpointTests(CatalogApiFactory factory)
    {
        _client = factory.CreateClient();
        foreach (var (sku, name, category) in new[] { ("A-1", "Alpha", "toys"), ("B-1", "Beta", "toys"), ("C-1", "Gamma", "tools") })
        {
            factory.Products.Add(Product.Create(
                Sku.Create(sku),
                name,
                "desc",
                category,
                null,
                [Money.Create(10m, Currency.Create("BRL")), Money.Create(2m, Currency.Create("USD"))]));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetProducts_WithCategoryAndPaging_ReturnsPricedItemsAndNextCursor()
    {
        var firstPage = await Get("/api/v1/products?category=toys&currency=USD&limit=1");

        firstPage.StatusCode.ShouldBe(HttpStatusCode.OK);
        var first = await firstPage.Content.ReadFromJsonAsync<JsonElement>(Token);
        first.GetProperty("items").GetArrayLength().ShouldBe(1);
        first.GetProperty("items")[0].GetProperty("sku").GetString().ShouldBe("A-1");
        first.GetProperty("items")[0].GetProperty("price").GetProperty("currency").GetString().ShouldBe("USD");
        var next = first.GetProperty("next").GetString();
        next.ShouldNotBeNull();

        var secondPage = await Get($"/api/v1/products?category=toys&limit=1&after={Uri.EscapeDataString(next)}");

        var second = await secondPage.Content.ReadFromJsonAsync<JsonElement>(Token);
        second.GetProperty("items")[0].GetProperty("sku").GetString().ShouldBe("B-1");
        second.GetProperty("next").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetProduct_ExistingSku_ReturnsProductWithEtagOfItsVersion()
    {
        var response = await Get("/api/v1/products/a-1");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        body.GetProperty("name").GetString().ShouldBe("Alpha");
        body.GetProperty("price").GetProperty("amount").GetDecimal().ShouldBe(10m);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetProduct_UnknownSku_ReturnsNotFoundProblem()
    {
        var response = await Get("/api/v1/products/NOPE");

        await AssertProblem(response, HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetProducts_UnknownCurrency_ReturnsBadRequestProblem()
    {
        var response = await Get("/api/v1/products?currency=EUR");

        await AssertProblem(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OpenApiDocument_DevelopmentHost_ListsBothProductEndpoints()
    {
        var response = await Get("/openapi/v1.json");

        var document = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var paths = document.GetProperty("paths");
        paths.TryGetProperty("/api/v1/products", out _).ShouldBeTrue();
        paths.TryGetProperty("/api/v1/products/{sku}", out _).ShouldBeTrue();
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Token);
        problem!.Status.ShouldBe((int)status);
    }

    private Task<HttpResponseMessage> Get(string url) => _client.GetAsync(url, Token);

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
