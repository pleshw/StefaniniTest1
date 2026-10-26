using System.Net.Http.Json;
using System.Text.Json;
using Catalog.Application.Products;
using Catalog.Domain;
using Catalog.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace Catalog.IntegrationTests;

/// <summary>The batch endpoint against a real replica set: one request must be one <c>find</c>.</summary>
public sealed class PriceQueryMongoTests : IClassFixture<MongoFixture>, IDisposable
{
    private readonly MongoFixture _mongo;
    private readonly IMongoDatabase _database;
    private readonly MongoBackedApiFactory _factory;

    public PriceQueryMongoTests(MongoFixture mongo)
    {
        _mongo = mongo;
        _database = mongo.NewDatabase();
        _factory = new MongoBackedApiFactory(mongo, _database);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostPricesQuery_ThreeSkus_RunsExactlyOneFindOnProducts()
    {
        var repository = new ProductRepository(_mongo.Client, _database);
        var audit = new AuditStamp("admin", DateTimeOffset.UnixEpoch);
        foreach (var sku in new[] { "A", "B", "C" })
        {
            var product = Product.Create(
                Sku.Create(sku), $"Product {sku}", "desc", "toys", null, [Money.Create(10m, Currency.Create("BRL"))]);
            await repository.SaveAsync(product, null, audit, [], Token);
        }

        var client = _factory.CreateClient();
        var before = _mongo.Commands.Count;
        var response = await client.PostAsJsonAsync(
            "/api/v1/products/prices:query", new { currency = "BRL", skus = new List<string> { "A", "B", "C" } }, Token);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        body.GetProperty("items").GetArrayLength().ShouldBe(3);
        var finds = _mongo.Commands.Skip(before)
            .Where(command => command.CommandName == "find" && command.DatabaseName == _database.DatabaseNamespace.DatabaseName);
        finds.Count().ShouldBe(1);
    }

    public void Dispose() => _factory.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class MongoBackedApiFactory(MongoFixture mongo, IMongoDatabase database) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMongoClient>();
                services.RemoveAll<IMongoDatabase>();
                services.AddSingleton(mongo.Client);
                services.AddSingleton(database);
            });
        }
    }
}
