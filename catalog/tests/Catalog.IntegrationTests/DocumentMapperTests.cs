using Catalog.Application.Products;
using Catalog.Domain;
using Catalog.Infrastructure.Persistence;

namespace Catalog.IntegrationTests;

/// <summary>Mapperly document mappings checked without a database, so they run as unit tests.</summary>
public sealed class DocumentMapperTests
{
    private static readonly DateTimeOffset _at = new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-3));

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDocument_ThenToDomain_RoundTripsTheProductAndStampsTheAuditInUtc()
    {
        var product = Product.Restore(
            Sku.Create("SKU-1"),
            "Keyboard",
            "desc",
            "peripherals",
            new Dictionary<string, string> { ["layout"] = "abnt2" },
            [Money.Create(19.99m, Currency.Create("BRL"))],
            isActive: true,
            version: 3);

        var document = product.ToDocument(new AuditStamp("admin", _at));
        var restored = document.ToDomain();

        document.Sku.ShouldBe("SKU-1");
        document.Prices.Single().Currency.ShouldBe("BRL");
        document.UpdatedBy.ShouldBe("admin");
        document.UpdatedAt.ShouldBe(_at.UtcDateTime);
        document.UpdatedAt.Kind.ShouldBe(DateTimeKind.Utc);
        restored.Sku.ShouldBe(product.Sku);
        restored.Prices.ShouldBe(product.Prices);
        restored.Attributes["layout"].ShouldBe("abnt2");
        restored.Version.ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDocument_PriceHistoryEntry_StoresCodesAndUtcTime()
    {
        var entry = new PriceHistoryEntry(Sku.Create("SKU-1"), Currency.Create("USD"), null, 5m, 2, "admin", _at, "trace");

        var document = entry.ToDocument();

        document.Sku.ShouldBe("SKU-1");
        document.Currency.ShouldBe("USD");
        document.OldPrice.ShouldBeNull();
        document.ChangedAt.ShouldBe(_at.UtcDateTime);
        document.ChangedAt.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToSummary_Document_ReadsSkuAndPricesAsDomainValues()
    {
        var document = new ProductDocument
        {
            Sku = "SKU-1",
            Name = "Keyboard",
            Category = "peripherals",
            Prices = [new PriceDocument { Currency = "BRL", Amount = 10m }],
            IsActive = true,
            Version = 4,
        };

        var summary = document.ToSummary();

        summary.Sku.ShouldBe(Sku.Create("SKU-1"));
        summary.Name.ShouldBe("Keyboard");
        summary.Version.ShouldBe(4);
        summary.Prices.ShouldBe([Money.Create(10m, Currency.Create("BRL"))]);
    }
}
