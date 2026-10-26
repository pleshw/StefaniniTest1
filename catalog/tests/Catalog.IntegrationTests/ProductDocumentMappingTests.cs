using Catalog.Infrastructure.Persistence;
using MongoDB.Bson;

namespace Catalog.IntegrationTests;

/// <summary>BSON class-map checks that need no database, so they run as unit tests.</summary>
public sealed class ProductDocumentMappingTests
{
    public ProductDocumentMappingTests()
    {
        CatalogClassMaps.Register();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToBsonDocument_ProductWithPrice_StoresMoneyAsDecimal128UnderPlanFieldNames()
    {
        var document = new ProductDocument
        {
            Sku = "SKU-1",
            Name = "Keyboard",
            Description = "desc",
            Category = "peripherals",
            Attributes = new Dictionary<string, string> { ["layout"] = "abnt2" },
            Prices = [new PriceDocument { Currency = "BRL", Amount = 19.99m }],
            IsActive = true,
            Version = 3,
            UpdatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            UpdatedBy = "admin",
        };

        var bson = document.ToBsonDocument();

        bson.Names.ShouldBe(["_id", "sku", "name", "description", "category", "attributes", "prices", "isActive", "version", "updatedAt", "updatedBy"], ignoreOrder: true);
        var price = bson["prices"].AsBsonArray.Single().AsBsonDocument;
        price["amount"].BsonType.ShouldBe(BsonType.Decimal128);
        price["currency"].AsString.ShouldBe("BRL");
        bson["version"].BsonType.ShouldBe(BsonType.Int32);
        bson["updatedAt"].BsonType.ShouldBe(BsonType.DateTime);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToBsonDocument_PriceHistoryEntry_StoresAmountsAsDecimal128UnderPlanFieldNames()
    {
        var document = new PriceHistoryDocument
        {
            Sku = "SKU-1",
            Currency = "BRL",
            OldPrice = 10m,
            NewPrice = 12m,
            Version = 2,
            ChangedBy = "admin",
            ChangedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            TraceId = "abc",
        };

        var bson = document.ToBsonDocument();

        bson["oldPrice"].BsonType.ShouldBe(BsonType.Decimal128);
        bson["newPrice"].BsonType.ShouldBe(BsonType.Decimal128);
        bson.Contains("changedAt").ShouldBeTrue();
        bson.Contains("traceId").ShouldBeTrue();
    }
}
