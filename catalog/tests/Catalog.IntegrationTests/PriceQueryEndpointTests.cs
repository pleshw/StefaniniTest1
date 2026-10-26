using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Catalog.IntegrationTests;

public sealed class PriceQueryEndpointTests : IClassFixture<CatalogApiFactory>
{
    private const string _url = "/api/v1/products/prices:query";

    private readonly HttpClient _client;

    public PriceQueryEndpointTests(CatalogApiFactory factory)
    {
        _client = factory.CreateClient();
        factory.Products.Add(Product.Create(
            Sku.Create("A-1"), "Alpha", "desc", "toys", null, [Money.Create(10m, Currency.Create("BRL"))]));
        var inactive = Product.Create(
            Sku.Create("B-1"), "Beta", "desc", "toys", null, [Money.Create(5m, Currency.Create("BRL"))]);
        inactive.Deactivate();
        factory.Products.Add(inactive);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostPricesQuery_FoundInactiveAndMissingSkus_ReturnsItemsAndMissing()
    {
        var response = await _client.PostAsJsonAsync(_url, new { currency = "BRL", skus = new List<string> { "a-1", "B-1", "NOPE" } }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var items = body.GetProperty("items");
        items.GetArrayLength().ShouldBe(2);
        items[0].GetProperty("sku").GetString().ShouldBe("A-1");
        items[0].GetProperty("unitPrice").GetDecimal().ShouldBe(10m);
        items[0].GetProperty("currency").GetString().ShouldBe("BRL");
        items[0].GetProperty("isActive").GetBoolean().ShouldBeTrue();
        items[1].GetProperty("isActive").GetBoolean().ShouldBeFalse();
        body.GetProperty("missing").EnumerateArray().Select(sku => sku.GetString()).ShouldBe(["NOPE"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostPricesQuery_NoSkus_ReturnsBadRequestProblem()
    {
        var response = await _client.PostAsJsonAsync(_url, new { currency = "BRL", skus = Array.Empty<string>() }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Token);
        problem!.Status.ShouldBe(400);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostPricesQuery_MoreThanOneHundredSkus_ReturnsBadRequest()
    {
        var skus = Enumerable.Range(1, 101).Select(index => $"S{index}");

        var response = await _client.PostAsJsonAsync(_url, new { currency = "BRL", skus }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
