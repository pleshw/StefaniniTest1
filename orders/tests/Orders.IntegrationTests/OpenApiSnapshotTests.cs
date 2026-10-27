using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orders.IntegrationTests;

/// <summary>
/// The committed document is what the BFF generates its Orders client from, so any API change must show up in review.
/// To refresh it, build Orders.Api (the build rewrites <c>orders/openapi/orders.v1.json</c>).
/// </summary>
public sealed class OpenApiSnapshotTests : IAsyncDisposable
{
    private readonly OrdersApiFactory _factory = new();
    private readonly HttpClient _client;

    public OpenApiSnapshotTests()
    {
        _client = _factory.CreateClient();
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
    public async Task OpenApiDocument_Generated_DescribesEveryOrdersEndpoint()
    {
        var document = JsonNode.Parse(await _client.GetStringAsync("/openapi/v1.json", Token))!;

        var operations = document["paths"]!.AsObject()
            .SelectMany(path => path.Value!.AsObject().Select(operation => $"{operation.Key.ToUpperInvariant()} {path.Key}"))
            .ToList();

        operations.ShouldBe(
        [
            "GET /api/v1/orders",
            "POST /api/v1/orders",
            "POST /api/v1/orders/quote",
            "GET /api/v1/orders/{id}",
            "GET /api/v1/orders/{id}/history",
            "POST /api/v1/admin/orders",
            "POST /api/v1/admin/orders/{id}/confirm",
            "POST /api/v1/admin/orders/{id}/hold",
            "POST /api/v1/admin/orders/{id}/block",
            "POST /api/v1/admin/orders/{id}/cancel",
            "GET /api/v1/admin/discount-rules",
            "POST /api/v1/admin/discount-rules",
            "GET /api/v1/admin/discount-rules/{id}",
            "PUT /api/v1/admin/discount-rules/{id}",
            "POST /api/v1/admin/discount-rules/{id}/deactivate",
            "POST /api/v1/admin/discount-rules/preview",
            "GET /api/v1/admin/confirmation-rules",
            "POST /api/v1/admin/confirmation-rules",
            "GET /api/v1/admin/confirmation-rules/{id}",
            "PUT /api/v1/admin/confirmation-rules/{id}",
            "POST /api/v1/admin/confirmation-rules/{id}/deactivate",
            "GET /api/v1/admin/dead-letters",
            "POST /api/v1/admin/dead-letters/{messageId}/replay",
            "GET /api/v1/admin/events",
        ],
        ignoreOrder: true);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
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

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), "orders", "openapi", "orders.v1.json");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
