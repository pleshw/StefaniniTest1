using Catalog.Domain;
using Riok.Mapperly.Abstractions;

namespace Catalog.Application.Products;

[Mapper]
public static partial class ProductMapper
{
    [MapperIgnoreSource(nameof(Product.DomainEvents))]
    public static partial ProductDetailDto ToDetailDto(this Product product);

    public static ProductDto ToDto(this Product product, Currency currency) =>
        product.ToDto(product.Prices.FirstOrDefault(candidate => candidate.Currency == currency)?.ToDto());

    public static partial PriceDto ToDto(this Money money);

    [MapperIgnoreSource(nameof(ProductSummary.Prices))]
    public static partial PriceQueryItem ToPriceQueryItem(this ProductSummary product, decimal unitPrice, string currency);

    [MapperIgnoreSource(nameof(Product.Prices))]
    [MapperIgnoreSource(nameof(Product.DomainEvents))]
    private static partial ProductDto ToDto(this Product product, PriceDto? price);

    private static string SkuToString(Sku sku) => sku.Value;

    private static string CurrencyToString(Currency currency) => currency.Code;
}
