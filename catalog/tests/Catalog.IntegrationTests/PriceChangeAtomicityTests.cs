using Catalog.Application.Products;
using Catalog.Domain;
using Catalog.Infrastructure.Persistence;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Catalog.IntegrationTests;

public sealed class PriceChangeAtomicityTests : IClassFixture<MongoFixture>
{
    private static readonly AuditStamp _audit = new("admin", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

    private readonly IMongoDatabase _database;
    private readonly ProductRepository _repository;

    public PriceChangeAtomicityTests(MongoFixture mongo)
    {
        _database = mongo.NewDatabase();
        _repository = new ProductRepository(mongo.Client, _database);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SaveAsync_HistoryInsertFails_RollsBackThePriceChange()
    {
        var brl = Currency.Create("BRL");
        var product = Product.Create(Sku.Create("SKU-1"), "Keyboard", "A keyboard", "peripherals", null, [Money.Create(100m, brl)]);
        await _repository.SaveAsync(product, null, _audit, [], Token);
        await MakeHistoryRejectRowsForVersionTwo();
        product.ChangePrice(Money.Create(120m, brl));
        var history = new PriceHistoryEntry(product.Sku, brl, 100m, 120m, product.Version, "admin", _audit.UpdatedAt, null);

        await Should.ThrowAsync<MongoBulkWriteException<PriceHistoryDocument>>(
            () => _repository.SaveAsync(product, 1, _audit, [history], Token));

        var stored = await _repository.GetBySkuAsync(product.Sku, Token);
        stored.ShouldNotBeNull();
        stored.Version.ShouldBe(1);
        stored.Prices.Single().Amount.ShouldBe(100m);
    }

    private async Task MakeHistoryRejectRowsForVersionTwo()
    {
        var rows = _database.GetCollection<BsonDocument>(CatalogCollections.PriceHistory);
        await rows.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("sku").Ascending("version"),
                new CreateIndexOptions { Unique = true }),
            cancellationToken: Token);
        await rows.InsertOneAsync(new BsonDocument { ["sku"] = "SKU-1", ["version"] = 2 }, cancellationToken: Token);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
