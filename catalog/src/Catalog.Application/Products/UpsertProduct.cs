using System.Diagnostics;
using System.Globalization;
using Catalog.Domain;

namespace Catalog.Application.Products;

public sealed record PriceInput(string? Currency, decimal? Amount);

public sealed record UpsertProductCommand(
    string Sku,
    string? IfMatch,
    string? Name,
    string? Description,
    string? Category,
    IReadOnlyDictionary<string, string>? Attributes,
    IReadOnlyList<PriceInput>? Prices,
    string UpdatedBy);

public sealed record ProductDetailDto(
    string Sku,
    string Name,
    string Description,
    string Category,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<PriceDto> Prices,
    bool IsActive,
    int Version)
{
    public static ProductDetailDto From(Product product) => new(
        product.Sku.Value,
        product.Name,
        product.Description,
        product.Category,
        product.Attributes,
        product.Prices.Select(price => new PriceDto(price.Amount, price.Currency.Code)).ToList(),
        product.IsActive,
        product.Version);
}

public sealed record UpsertOutcome(ProductDetailDto Product, bool Created);

public sealed class UpsertProductHandler(IProductRepository repository, CurrencyPolicy currencies, TimeProvider clock)
{
    public async Task<Result<UpsertOutcome>> HandleAsync(UpsertProductCommand command, CancellationToken cancellationToken)
    {
        if (!Sku.TryCreate(command.Sku, out var sku))
        {
            return Invalid("sku is not valid.");
        }

        int? ifMatchVersion = null;

        if (command.IfMatch is not null)
        {
            ifMatchVersion = ParseVersion(command.IfMatch);

            if (ifMatchVersion is null)
            {
                return Invalid("If-Match must be the ETag of a product version.");
            }
        }

        try
        {
            var prices = ToMoney(command.Prices);
            var existing = await repository.GetBySkuAsync(sku!, cancellationToken);

            return existing is null
                ? await CreateAsync(sku!, ifMatchVersion, command, prices, cancellationToken)
                : await ReplaceAsync(existing, ifMatchVersion, command, prices, cancellationToken);
        }
        catch (DomainException exception)
        {
            return Invalid(exception.Message);
        }
        catch (ConcurrencyException)
        {
            return Result.Fail<UpsertOutcome>(ErrorKind.PreconditionFailed, "The product was changed by someone else; reload it and retry.");
        }
    }

    private async Task<Result<UpsertOutcome>> CreateAsync(
        Sku sku, int? ifMatchVersion, UpsertProductCommand command, List<Money> prices, CancellationToken cancellationToken)
    {
        if (ifMatchVersion is not null)
        {
            return Result.Fail<UpsertOutcome>(ErrorKind.PreconditionFailed, $"Product {sku} does not exist.");
        }

        var product = Product.Create(sku, command.Name, command.Description, command.Category, command.Attributes, prices);
        await repository.SaveAsync(product, null, AuditFor(command), [], cancellationToken);

        return Result.Ok(new UpsertOutcome(ProductDetailDto.From(product), true));
    }

    private async Task<Result<UpsertOutcome>> ReplaceAsync(
        Product product, int? ifMatchVersion, UpsertProductCommand command, List<Money> prices, CancellationToken cancellationToken)
    {
        if (ifMatchVersion is null)
        {
            return Result.Fail<UpsertOutcome>(ErrorKind.PreconditionRequired, "If-Match is required to replace an existing product.");
        }

        if (ifMatchVersion != product.Version)
        {
            return Result.Fail<UpsertOutcome>(ErrorKind.PreconditionFailed, $"Product {product.Sku} is at version {product.Version}.");
        }

        var expectedVersion = product.Version;
        product.Replace(command.Name, command.Description, command.Category, command.Attributes, prices);

        if (product.Version != expectedVersion)
        {
            var audit = AuditFor(command);
            var history = product.DomainEvents.OfType<PriceChanged>().Select(change => ToHistory(change, audit)).ToList();
            await repository.SaveAsync(product, expectedVersion, audit, history, cancellationToken);
        }

        return Result.Ok(new UpsertOutcome(ProductDetailDto.From(product), false));
    }

    private AuditStamp AuditFor(UpsertProductCommand command) => new(command.UpdatedBy, clock.GetUtcNow());

    private static PriceHistoryEntry ToHistory(PriceChanged change, AuditStamp audit) => new(
        change.Sku,
        change.NewPrice.Currency,
        change.OldPrice?.Amount,
        change.NewPrice.Amount,
        change.Version,
        audit.UpdatedBy,
        audit.UpdatedAt,
        Activity.Current?.TraceId.ToString());

    private List<Money> ToMoney(IReadOnlyList<PriceInput>? inputs)
    {
        if (inputs is null)
        {
            throw new DomainException("prices is required (it may be empty).");
        }

        return inputs.Select(input =>
        {
            var currency = currencies.TryParse(input.Currency, out var parsed)
                ? parsed!
                : throw new DomainException($"Currency '{input.Currency}' is not supported.");

            return Money.Create(input.Amount ?? throw new DomainException("Every price needs an amount."), currency);
        }).ToList();
    }

    /// <summary>Reads <c>"3"</c> or <c>W/"3"</c> as version 3.</summary>
    private static int? ParseVersion(string etag)
    {
        var value = etag.Trim();
        value = value.StartsWith("W/", StringComparison.Ordinal) ? value[2..] : value;

        return value.Length > 2 && value[0] == '"' && value[^1] == '"'
            && int.TryParse(value[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : null;
    }

    private static Result<UpsertOutcome> Invalid(string message) => Result.Fail<UpsertOutcome>(ErrorKind.Validation, message);
}
