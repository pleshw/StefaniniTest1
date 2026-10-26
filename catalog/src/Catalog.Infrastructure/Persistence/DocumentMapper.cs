using Catalog.Application.Products;
using Catalog.Domain;
using Riok.Mapperly.Abstractions;

namespace Catalog.Infrastructure.Persistence;

/// <summary>
/// Maps aggregates and read models to and from their MongoDB documents. Products are rebuilt through
/// <see cref="Product.Restore"/> so the domain invariants run on every read.
/// </summary>
[Mapper]
internal static partial class DocumentMapper
{
    public static ProductDocument ToDocument(this Product product, AuditStamp audit) =>
        product.ToDocument(audit.UpdatedBy, audit.UpdatedAt.UtcDateTime);

    [MapperIgnoreTarget(nameof(PriceHistoryDocument.Id))]
    public static partial PriceHistoryDocument ToDocument(this PriceHistoryEntry entry);

    [MapperIgnoreSource(nameof(ProductDocument.Id))]
    [MapperIgnoreSource(nameof(ProductDocument.Description))]
    [MapperIgnoreSource(nameof(ProductDocument.Attributes))]
    [MapperIgnoreSource(nameof(ProductDocument.UpdatedAt))]
    [MapperIgnoreSource(nameof(ProductDocument.UpdatedBy))]
    public static partial ProductSummary ToSummary(this ProductDocument document);

    public static Product ToDomain(this ProductDocument document) => Product.Restore(
        Sku.Create(document.Sku),
        document.Name,
        document.Description,
        document.Category,
        document.Attributes,
        document.Prices.Select(ToMoney),
        document.IsActive,
        document.Version);

    [MapperIgnoreTarget(nameof(ProductDocument.Id))]
    [MapperIgnoreSource(nameof(Product.DomainEvents))]
    private static partial ProductDocument ToDocument(this Product product, string updatedBy, DateTime updatedAt);

    private static Money ToMoney(PriceDocument price) => Money.Create(price.Amount, Currency.Create(price.Currency));

    private static string SkuToString(Sku sku) => sku.Value;

    private static Sku StringToSku(string value) => Sku.Create(value);

    private static string CurrencyToString(Currency currency) => currency.Code;

    private static DateTime ToUtc(DateTimeOffset value) => value.UtcDateTime;
}
