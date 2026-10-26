using Catalog.Application.Products;
using Catalog.Domain;
using Catalog.Infrastructure.Persistence;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Catalog.IntegrationTests;

public sealed class ProductRepositoryTests : IClassFixture<MongoFixture>
{
    private static readonly AuditStamp _audit = new("admin", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

    private readonly MongoFixture _mongo;
    private readonly IMongoDatabase _database;
    private readonly ProductRepository _repository;

    public ProductRepositoryTests(MongoFixture mongo)
    {
        _mongo = mongo;
        _database = mongo.NewDatabase();
        _repository = new ProductRepository(mongo.Client, _database);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SaveAsync_NewProduct_CanBeReadBackWithPricesAndAttributes()
    {
        var product = NewProduct("SKU-1");

        await _repository.SaveAsync(product, null, _audit, [], Token);
        var stored = await _repository.GetBySkuAsync(product.Sku, Token);

        stored.ShouldNotBeNull();
        stored.Name.ShouldBe("Keyboard");
        stored.Version.ShouldBe(1);
        stored.IsActive.ShouldBeTrue();
        stored.Attributes["layout"].ShouldBe("abnt2");
        stored.Prices.ShouldBe(product.Prices);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetBySkuAsync_UnknownSku_ReturnsNull()
    {
        var stored = await _repository.GetBySkuAsync(Sku.Create("NOPE"), Token);

        stored.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SaveAsync_SkuAlreadyStored_ThrowsConcurrencyException()
    {
        await new MongoIndexInitializer(_database).StartAsync(Token);
        await _repository.SaveAsync(NewProduct("SKU-1"), null, _audit, [], Token);

        await Should.ThrowAsync<ConcurrencyException>(
            () => _repository.SaveAsync(NewProduct("SKU-1"), null, _audit, [], Token));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SaveAsync_MatchingVersion_ReplacesStoredProduct()
    {
        var product = NewProduct("SKU-1");
        await _repository.SaveAsync(product, null, _audit, [], Token);
        product.Rename("Mechanical keyboard");

        await _repository.SaveAsync(product, 1, _audit, [], Token);

        var stored = await _repository.GetBySkuAsync(product.Sku, Token);
        stored.ShouldNotBeNull();
        stored.Name.ShouldBe("Mechanical keyboard");
        stored.Version.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SaveAsync_StaleVersion_ThrowsConcurrencyExceptionAndKeepsStoredProduct()
    {
        var product = NewProduct("SKU-1");
        await _repository.SaveAsync(product, null, _audit, [], Token);
        product.Rename("Changed by someone else");
        await _repository.SaveAsync(product, 1, _audit, [], Token);
        product.Rename("My stale edit");

        await Should.ThrowAsync<ConcurrencyException>(() => _repository.SaveAsync(product, 1, _audit, [], Token));

        var stored = await _repository.GetBySkuAsync(product.Sku, Token);
        stored.ShouldNotBeNull();
        stored.Name.ShouldBe("Changed by someone else");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SaveAsync_WithPriceHistory_StoresHistoryRowsWithTheProduct()
    {
        var product = NewProduct("SKU-1");
        await _repository.SaveAsync(product, null, _audit, [], Token);
        product.ChangePrice(Money.Create(120m, Currency.Create("BRL")));
        var history = new PriceHistoryEntry(
            product.Sku, Currency.Create("BRL"), 100m, 120m, product.Version, "admin", _audit.UpdatedAt, "trace-1");

        await _repository.SaveAsync(product, 1, _audit, [history], Token);

        var rows = await _database.GetCollection<BsonDocument>(CatalogCollections.PriceHistory)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .ToListAsync(Token);
        var row = rows.ShouldHaveSingleItem();
        row["sku"].AsString.ShouldBe("SKU-1");
        row["oldPrice"].AsDecimal.ShouldBe(100m);
        row["newPrice"].AsDecimal.ShouldBe(120m);
        row["version"].AsInt32.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SaveAsync_StaleVersionWithPriceHistory_WritesNoHistory()
    {
        var product = NewProduct("SKU-1");
        await _repository.SaveAsync(product, null, _audit, [], Token);
        var history = new PriceHistoryEntry(
            product.Sku, Currency.Create("BRL"), 100m, 120m, 2, "admin", _audit.UpdatedAt, null);

        await Should.ThrowAsync<ConcurrencyException>(() => _repository.SaveAsync(product, 99, _audit, [history], Token));

        var count = await _database.GetCollection<BsonDocument>(CatalogCollections.PriceHistory)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Token);
        count.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetManyAsync_SeveralSkus_RunsOneFindWithAProjection()
    {
        foreach (var sku in new[] { "A", "B", "C" })
        {
            await _repository.SaveAsync(NewProduct(sku), null, _audit, [], Token);
        }

        var before = _mongo.Commands.Count;
        var found = await _repository.GetManyAsync([Sku.Create("A"), Sku.Create("C"), Sku.Create("MISSING")], Token);

        found.Select(product => product.Sku.Value).ShouldBe(["A", "C"], ignoreOrder: true);
        found[0].Prices.ShouldNotBeEmpty();
        var finds = _mongo.Commands.Skip(before)
            .Where(command => command.CommandName == "find" && command.DatabaseName == _database.DatabaseNamespace.DatabaseName)
            .ToList();
        var find = finds.ShouldHaveSingleItem();
        var projection = find.Command["projection"].AsBsonDocument;
        projection.Contains("description").ShouldBeFalse();
        projection.Contains("attributes").ShouldBeFalse();
        projection["prices"].ToBoolean().ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SearchAsync_CategoryAndCursor_ReturnsActiveProductsInNameThenSkuOrder()
    {
        await _repository.SaveAsync(NewProduct("B", "Beta"), null, _audit, [], Token);
        await _repository.SaveAsync(NewProduct("A2", "Alpha"), null, _audit, [], Token);
        await _repository.SaveAsync(NewProduct("A1", "Alpha"), null, _audit, [], Token);
        var inactive = NewProduct("Z", "Zeta");
        inactive.Deactivate();
        await _repository.SaveAsync(inactive, null, _audit, [], Token);

        var all = await _repository.SearchAsync(new ProductSearch(null, "peripherals", null, 10), Token);
        var afterFirst = await _repository.SearchAsync(
            new ProductSearch(null, "peripherals", new ProductCursor("Alpha", "A1"), 10), Token);

        all.Select(product => product.Sku.Value).ShouldBe(["A1", "A2", "B"]);
        afterFirst.Select(product => product.Sku.Value).ShouldBe(["A2", "B"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SearchAsync_TextAndLimit_UsesTheTextIndexAndStopsAtTheLimit()
    {
        await _repository.SaveAsync(NewProduct("A", "Mechanical keyboard"), null, _audit, [], Token);
        await _repository.SaveAsync(NewProduct("B", "Keyboard cover"), null, _audit, [], Token);
        await _repository.SaveAsync(NewProduct("C", "Mouse"), null, _audit, [], Token);
        await new MongoIndexInitializer(_database).StartAsync(Token);

        var matches = await _repository.SearchAsync(new ProductSearch("keyboard", null, null, 1), Token);

        matches.Select(product => product.Sku.Value).ShouldBe(["B"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StartAsync_ReplicaSetDatabase_CreatesTheRequiredIndexes()
    {
        var initializer = new MongoIndexInitializer(_database);

        await initializer.StartAsync(Token);

        (await IndexNames(CatalogCollections.Products)).ShouldBe(
            ["_id_", "ux_products_sku", "ix_products_category_name", "tx_products_name_description"], ignoreOrder: true);
        (await IndexNames(CatalogCollections.PriceHistory)).ShouldBe(["_id_", "ix_price_history_sku_changed_at"], ignoreOrder: true);
        (await IndexNames(CatalogCollections.IdempotencyRequests)).ShouldBe(
            ["_id_", "ux_idempotency_scope_key", "ttl_idempotency_expires_at"], ignoreOrder: true);
    }

    private async Task<List<string>> IndexNames(string collection)
    {
        var indexes = await (await _database.GetCollection<BsonDocument>(collection).Indexes.ListAsync(Token)).ToListAsync(Token);
        return indexes.Select(index => index["name"].AsString).ToList();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Product NewProduct(string sku, string name = "Keyboard") => Product.Create(
        Sku.Create(sku),
        name,
        "A keyboard",
        "peripherals",
        new Dictionary<string, string> { ["layout"] = "abnt2" },
        [Money.Create(100m, Currency.Create("BRL")), Money.Create(20m, Currency.Create("USD"))]);
}
