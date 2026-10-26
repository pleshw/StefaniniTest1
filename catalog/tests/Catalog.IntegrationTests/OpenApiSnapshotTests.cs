using System.Text.Json;
using System.Text.Json.Nodes;

namespace Catalog.IntegrationTests;

/// <summary>
/// The committed document is what Orders generates its client from, so any API change must show up in review.
/// To refresh it, build Catalog.Api (the build rewrites <c>catalog/openapi/catalog.v1.json</c>).
/// </summary>
public sealed class OpenApiSnapshotTests : IClassFixture<CatalogApiFactory>
{
    private readonly HttpClient _client;

    public OpenApiSnapshotTests(CatalogApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OpenApiDocument_Generated_MatchesTheCommittedSnapshot()
    {
        var generated = await _client.GetStringAsync("/openapi/v1.json", Token);

        var committed = await File.ReadAllTextAsync(SnapshotPath(), Token);

        Normalize(generated).ShouldBe(Normalize(committed));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OpenApiDocument_Generated_DescribesEveryCatalogEndpoint()
    {
        var document = JsonNode.Parse(await _client.GetStringAsync("/openapi/v1.json", Token))!;

        var operations = document["paths"]!.AsObject()
            .SelectMany(path => path.Value!.AsObject().Select(operation => $"{operation.Key.ToUpperInvariant()} {path.Key}"))
            .Order()
            .ToList();

        operations.ShouldBe(
        [
            "GET /api/v1/products",
            "GET /api/v1/products/{sku}",
            "PATCH /api/v1/admin/products/{sku}/price",
            "POST /api/v1/admin/products/{sku}/deactivate",
            "POST /api/v1/products/prices:query",
            "PUT /api/v1/admin/products/{sku}",
        ],
        ignoreOrder: true);
    }

    /// <summary>The served document adds <c>servers</c> from the request host; the build-time one has none.</summary>
    private static string Normalize(string json)
    {
        var document = JsonNode.Parse(json)!.AsObject();
        document.Remove("servers");

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string SnapshotPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), "catalog", "openapi", "catalog.v1.json");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
