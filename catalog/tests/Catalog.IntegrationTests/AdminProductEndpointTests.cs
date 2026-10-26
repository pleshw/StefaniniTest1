using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog.Domain;

namespace Catalog.IntegrationTests;

public sealed class AdminProductEndpointTests : IClassFixture<CatalogApiFactory>
{
    private const string _adminUser = "admin-1";

    private readonly CatalogApiFactory _factory;
    private readonly HttpClient _client;

    public AdminProductEndpointTests(CatalogApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutProduct_AbsentSku_CreatesItWith201LocationEtagAndUpdatedBy()
    {
        var response = await Put("/api/v1/admin/products/new-1", Body("Keyboard", 100m), admin: true);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldBe("/api/v1/products/NEW-1");
        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        body.GetProperty("prices")[0].GetProperty("amount").GetDecimal().ShouldBe(100m);
        _factory.Products.Audits["NEW-1"].UpdatedBy.ShouldBe(_adminUser);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutProduct_ExistingSkuWithMatchingIfMatch_ReplacesItWith200AndNewEtag()
    {
        await Put("/api/v1/admin/products/rep-1", Body("Keyboard", 100m), admin: true);

        var response = await Put("/api/v1/admin/products/rep-1", Body("Mechanical keyboard", 120m), admin: true, ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.ShouldBe("\"2\"");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        body.GetProperty("name").GetString().ShouldBe("Mechanical keyboard");
        _factory.Products.PriceHistory.ShouldContain(entry => entry.Sku.Value == "REP-1" && entry.NewPrice == 120m && entry.OldPrice == 100m);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutProduct_StaleIfMatch_Returns412Problem()
    {
        await Put("/api/v1/admin/products/stale-1", Body("Keyboard", 100m), admin: true);

        var response = await Put("/api/v1/admin/products/stale-1", Body("Other", 100m), admin: true, ifMatch: "\"9\"");

        await AssertProblem(response, HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutProduct_ExistingSkuWithoutIfMatch_Returns428Problem()
    {
        await Put("/api/v1/admin/products/need-1", Body("Keyboard", 100m), admin: true);

        var response = await Put("/api/v1/admin/products/need-1", Body("Keyboard", 100m), admin: true);

        await AssertProblem(response, HttpStatusCode.PreconditionRequired);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutProduct_InvalidBody_Returns400Problem()
    {
        var response = await Put("/api/v1/admin/products/bad-1", Body(" ", 100m), admin: true);

        await AssertProblem(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutProduct_NegativePrice_Returns400Problem()
    {
        var response = await Put("/api/v1/admin/products/bad-2", Body("Keyboard", -1m), admin: true);

        await AssertProblem(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutProduct_SignedInWithoutAdminRole_Returns403()
    {
        var response = await Put("/api/v1/admin/products/forbidden-1", Body("Keyboard", 100m), admin: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _factory.Products.GetBySkuAsync(Sku.Create("FORBIDDEN-1"), Token)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutProduct_Anonymous_Returns401()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/admin/products/anon-1", Body("Keyboard", 100m), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PutProduct_DefaultHost_ListsTheAdminRouteInOpenApi()
    {
        var response = await _client.GetAsync("/openapi/v1.json", Token);

        var document = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        document.GetProperty("paths").TryGetProperty("/api/v1/admin/products/{sku}", out _).ShouldBeTrue();
    }

    private async Task<HttpResponseMessage> Put(string url, object body, bool admin, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestAuthHandler.UserHeader, _adminUser);
        request.Headers.Add(TestAuthHandler.RolesHeader, admin ? "Admin" : "Shopper");

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await _client.SendAsync(request, Token);
    }

    private static object Body(string name, decimal brl) => new
    {
        name,
        description = "A keyboard",
        category = "peripherals",
        attributes = new Dictionary<string, string> { ["layout"] = "abnt2" },
        prices = new List<object> { new { currency = "BRL", amount = brl } },
    };

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        problem.GetProperty("status").GetInt32().ShouldBe((int)status);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
