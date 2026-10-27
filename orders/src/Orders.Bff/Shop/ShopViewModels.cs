using Orders.Bff.Clients.Catalog;
using Riok.Mapperly.Abstractions;

namespace Orders.Bff.Shop;

/// <summary>A product as the Shop page shows it: no stock-keeping details (attributes, version, active flag).</summary>
internal sealed record ShopProduct(string Sku, string Name, string Description, string Category, ShopPrice? Price);

internal sealed record ShopPrice(decimal Amount, string Currency);

internal sealed record ShopProductPage(IReadOnlyList<ShopProduct> Items, string? Next);

[Mapper]
internal static partial class ShopMapper
{
    // The generated page holds its items as nullable references; one mapping covers both.
    [MapperIgnoreSource(nameof(ProductDto.AdditionalProperties))]
    [MapperIgnoreSource(nameof(ProductDto.Attributes))]
    [MapperIgnoreSource(nameof(ProductDto.IsActive))]
    [MapperIgnoreSource(nameof(ProductDto.Version))]
    public static partial ShopProduct ToShop(this ProductDto? product);

    [MapperIgnoreSource(nameof(PriceDto.AdditionalProperties))]
    public static partial ShopPrice ToShop(this PriceDto price);

    [MapperIgnoreSource(nameof(ProductPage.AdditionalProperties))]
    public static partial ShopProductPage ToShop(this ProductPage page);
}
