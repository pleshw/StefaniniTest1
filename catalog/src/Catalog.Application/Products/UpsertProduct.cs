using System.Diagnostics;
using System.Globalization;
using Catalog.Domain;
using Catalog.Domain.Validation;
using FluentValidation;

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
    int Version);

public sealed record UpsertOutcome(ProductDetailDto Product, bool Created);

public sealed class PriceInputValidator : AbstractValidator<PriceInput>
{
    public PriceInputValidator(CurrencyPolicy currencies)
    {
        RuleFor(price => price.Currency).SupportedCurrency(currencies);
        RuleFor(price => price.Amount)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Every price needs an amount.")
            .GreaterThanOrEqualTo(0).WithMessage("Price must not be negative.");
    }
}

public sealed class UpsertProductCommandValidator : AbstractValidator<UpsertProductCommand>
{
    public UpsertProductCommandValidator(IValidator<PriceInput> priceValidator)
    {
        RuleFor(command => command.Sku).ValidSku();
        RuleFor(command => command.IfMatch)
            .Must(ifMatch => EntityTag.TryParseVersion(ifMatch!, out _))
            .When(command => command.IfMatch is not null)
            .WithMessage("If-Match must be the ETag of a product version.");
        RuleFor(command => command.Name).NotEmpty().WithMessage("Name must not be empty.");
        RuleFor(command => command.Category).NotEmpty().WithMessage("Category must not be empty.");
        RuleFor(command => command.Prices)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("prices is required (it may be empty).")
            .Must(prices => prices!.Select(price => price.Currency?.Trim().ToUpperInvariant()).Distinct().Count() == prices!.Count)
            .WithMessage("A product can have only one price per currency.");
        RuleForEach(command => command.Prices).SetValidator(priceValidator);
        RuleFor(command => command.UpdatedBy).NotEmpty();
    }
}

public sealed class UpsertProductHandler(
    IProductRepository repository,
    CurrencyPolicy currencies,
    TimeProvider clock,
    IValidator<UpsertProductCommand> validator)
{
    public async Task<Result<UpsertOutcome>> HandleAsync(UpsertProductCommand command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<UpsertOutcome>();
        }

        var sku = Sku.Create(command.Sku);
        int? ifMatchVersion = command.IfMatch is not null && EntityTag.TryParseVersion(command.IfMatch, out var version) ? version : null;

        try
        {
            var prices = command.Prices!.Select(price => Money.Create(price.Amount!.Value, currencies.Parse(price.Currency))).ToList();
            var existing = await repository.GetBySkuAsync(sku, cancellationToken);

            return existing is null
                ? await CreateAsync(sku, ifMatchVersion, command, prices, cancellationToken)
                : await ReplaceAsync(existing, ifMatchVersion, command, prices, cancellationToken);
        }
        catch (DomainException exception)
        {
            return Result.Fail<UpsertOutcome>(ErrorKind.Validation, exception.Message);
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

        return Result.Ok(new UpsertOutcome(product.ToDetailDto(), true));
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

        return Result.Ok(new UpsertOutcome(product.ToDetailDto(), false));
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
}

internal static class EntityTag
{
    /// <summary>Reads <c>"3"</c> or <c>W/"3"</c> as version 3.</summary>
    public static bool TryParseVersion(string etag, out int version)
    {
        var value = etag.Trim();
        value = value.StartsWith("W/", StringComparison.Ordinal) ? value[2..] : value;
        version = 0;

        return value.Length > 2 && value[0] == '"' && value[^1] == '"'
            && int.TryParse(value[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }
}
