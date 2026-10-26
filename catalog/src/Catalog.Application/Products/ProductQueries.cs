using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Catalog.Domain;

namespace Catalog.Application.Products;

public sealed record PriceDto(decimal Amount, string Currency);

public sealed record ProductDto(
    string Sku,
    string Name,
    string Description,
    string Category,
    IReadOnlyDictionary<string, string> Attributes,
    PriceDto? Price,
    bool IsActive,
    int Version)
{
    public static ProductDto From(Product product, Currency currency)
    {
        var price = product.Prices.FirstOrDefault(candidate => candidate.Currency == currency);

        return new ProductDto(
            product.Sku.Value,
            product.Name,
            product.Description,
            product.Category,
            product.Attributes,
            price is null ? null : new PriceDto(price.Amount, price.Currency.Code),
            product.IsActive,
            product.Version);
    }
}

public sealed record ProductPage(IReadOnlyList<ProductDto> Items, string? Next);

/// <summary>Where the previous page stopped: products are ordered by name, then SKU.</summary>
public sealed record ProductCursor(string Name, string Sku)
{
    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new[] { Name, Sku }));

    public static bool TryDecode(string value, out ProductCursor? cursor)
    {
        cursor = null;

        try
        {
            var parts = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value)));

            if (parts is not [not null, not null])
            {
                return false;
            }

            cursor = new ProductCursor(parts[0], parts[1]);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }
}

public sealed record ProductSearch(string? Text, string? Category, ProductCursor? After, int Limit);

public sealed record ListProductsQuery(string? Q, string? Category, string? Currency, string? After, int? Limit);

public sealed record GetProductQuery(string Sku, string? Currency);

public sealed class ListProductsHandler(IProductRepository repository, CurrencyPolicy currencies)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public async Task<Result<ProductPage>> HandleAsync(ListProductsQuery query, CancellationToken cancellationToken)
    {
        var limit = query.Limit ?? DefaultLimit;

        if (limit is < 1 or > MaxLimit)
        {
            return Result.Fail<ProductPage>(ErrorKind.Validation, $"limit must be between 1 and {MaxLimit}.");
        }

        var currency = query.Currency is null ? currencies.Default : null;

        if (query.Currency is not null && !currencies.TryParse(query.Currency, out currency))
        {
            return Result.Fail<ProductPage>(ErrorKind.Validation, $"Currency {query.Currency} is not supported.");
        }

        ProductCursor? after = null;

        if (query.After is not null && !ProductCursor.TryDecode(query.After, out after))
        {
            return Result.Fail<ProductPage>(ErrorKind.Validation, "after is not a valid cursor.");
        }

        var found = await repository.SearchAsync(
            new ProductSearch(query.Q, query.Category, after, limit + 1), cancellationToken);

        var page = found.Take(limit).ToList();
        var next = found.Count > limit ? new ProductCursor(page[^1].Name, page[^1].Sku.Value).Encode() : null;

        return Result.Ok(new ProductPage(page.Select(product => ProductDto.From(product, currency!)).ToList(), next));
    }
}

public sealed class GetProductHandler(IProductRepository repository, CurrencyPolicy currencies)
{
    public async Task<Result<ProductDto>> HandleAsync(GetProductQuery query, CancellationToken cancellationToken)
    {
        if (!Sku.TryCreate(query.Sku, out var sku))
        {
            return Result.Fail<ProductDto>(ErrorKind.Validation, "sku is not valid.");
        }

        var currency = query.Currency is null ? currencies.Default : null;

        if (query.Currency is not null && !currencies.TryParse(query.Currency, out currency))
        {
            return Result.Fail<ProductDto>(ErrorKind.Validation, $"Currency {query.Currency} is not supported.");
        }

        var product = await repository.GetBySkuAsync(sku!, cancellationToken);

        return product is null
            ? Result.Fail<ProductDto>(ErrorKind.NotFound, $"Product {sku} was not found.")
            : Result.Ok(ProductDto.From(product, currency!));
    }
}
