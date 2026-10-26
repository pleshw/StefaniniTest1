using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Catalog.Domain;
using Catalog.Domain.Validation;
using FluentValidation;

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
    int Version);

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

public sealed class ListProductsQueryValidator : AbstractValidator<ListProductsQuery>
{
    public ListProductsQueryValidator(CurrencyPolicy currencies)
    {
        RuleFor(query => query.Limit)
            .InclusiveBetween(1, ListProductsHandler.MaxLimit)
            .WithMessage($"limit must be between 1 and {ListProductsHandler.MaxLimit}.");
        RuleFor(query => query.Currency).SupportedCurrency(currencies).When(query => query.Currency is not null);
        RuleFor(query => query.After)
            .Must(after => ProductCursor.TryDecode(after!, out _))
            .When(query => query.After is not null)
            .WithMessage("after is not a valid cursor.");
    }
}

public sealed class GetProductQueryValidator : AbstractValidator<GetProductQuery>
{
    public GetProductQueryValidator(CurrencyPolicy currencies)
    {
        RuleFor(query => query.Sku).ValidSku();
        RuleFor(query => query.Currency).SupportedCurrency(currencies).When(query => query.Currency is not null);
    }
}

public sealed class ListProductsHandler(
    IProductRepository repository, CurrencyPolicy currencies, IValidator<ListProductsQuery> validator)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public async Task<Result<ProductPage>> HandleAsync(ListProductsQuery query, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<ProductPage>();
        }

        var limit = query.Limit ?? DefaultLimit;
        var currency = query.Currency is null ? currencies.Default : currencies.Parse(query.Currency);
        var after = query.After is not null && ProductCursor.TryDecode(query.After, out var cursor) ? cursor : null;

        var found = await repository.SearchAsync(
            new ProductSearch(query.Q, query.Category, after, limit + 1), cancellationToken);

        var page = found.Take(limit).ToList();
        var next = found.Count > limit ? new ProductCursor(page[^1].Name, page[^1].Sku.Value).Encode() : null;

        return Result.Ok(new ProductPage(page.Select(product => product.ToDto(currency)).ToList(), next));
    }
}

public sealed class GetProductHandler(
    IProductRepository repository, CurrencyPolicy currencies, IValidator<GetProductQuery> validator)
{
    public async Task<Result<ProductDto>> HandleAsync(GetProductQuery query, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<ProductDto>();
        }

        var sku = Sku.Create(query.Sku);
        var currency = query.Currency is null ? currencies.Default : currencies.Parse(query.Currency);
        var product = await repository.GetBySkuAsync(sku, cancellationToken);

        return product is null
            ? Result.Fail<ProductDto>(ErrorKind.NotFound, $"Product {sku} was not found.")
            : Result.Ok(product.ToDto(currency));
    }
}
