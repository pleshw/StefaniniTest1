using Catalog.Domain;
using Catalog.Domain.Validation;
using FluentValidation;

namespace Catalog.Application.Products;

public sealed record ChangePriceCommand(string Sku, string? IfMatch, string? Currency, decimal? Amount, string UpdatedBy);

public sealed class ChangePriceCommandValidator : AbstractValidator<ChangePriceCommand>
{
    public ChangePriceCommandValidator(CurrencyPolicy currencies)
    {
        RuleFor(command => command.Sku).ValidSku();
        RuleFor(command => command.IfMatch)
            .Must(ifMatch => EntityTag.TryParseVersion(ifMatch!, out _))
            .When(command => command.IfMatch is not null)
            .WithMessage("If-Match must be the ETag of a product version.");
        RuleFor(command => command.Currency).SupportedCurrency(currencies);
        RuleFor(command => command.Amount)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("amount is required.")
            .GreaterThanOrEqualTo(0).WithMessage("Price must not be negative.");
        RuleFor(command => command.UpdatedBy).NotEmpty();
    }
}

public sealed class ChangePriceHandler(
    IProductRepository repository,
    CurrencyPolicy currencies,
    TimeProvider clock,
    IValidator<ChangePriceCommand> validator)
{
    public async Task<Result<ProductDetailDto>> HandleAsync(ChangePriceCommand command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<ProductDetailDto>();
        }

        var sku = Sku.Create(command.Sku);
        var product = await repository.GetBySkuAsync(sku, cancellationToken);

        if (product is null)
        {
            return Result.Fail<ProductDetailDto>(ErrorKind.NotFound, $"Product {sku} does not exist.");
        }

        if (command.IfMatch is null)
        {
            return Result.Fail<ProductDetailDto>(ErrorKind.PreconditionRequired, "If-Match is required to change a price.");
        }

        return EntityTag.TryParseVersion(command.IfMatch, out var ifMatchVersion) && ifMatchVersion == product.Version
            ? await ChangeAsync(product, command, cancellationToken)
            : Result.Fail<ProductDetailDto>(ErrorKind.PreconditionFailed, $"Product {product.Sku} is at version {product.Version}.");
    }

    private async Task<Result<ProductDetailDto>> ChangeAsync(Product product, ChangePriceCommand command, CancellationToken cancellationToken)
    {
        var expectedVersion = product.Version;

        try
        {
            product.ChangePrice(Money.Create(command.Amount!.Value, currencies.Parse(command.Currency)));

            if (product.Version != expectedVersion)
            {
                var audit = new AuditStamp(command.UpdatedBy, clock.GetUtcNow());
                await repository.SaveAsync(product, expectedVersion, audit, ProductChanges.HistoryFor(product, audit), cancellationToken);
            }

            return Result.Ok(product.ToDetailDto());
        }
        catch (DomainException exception)
        {
            return Result.Fail<ProductDetailDto>(ErrorKind.Validation, exception.Message);
        }
        catch (ConcurrencyException)
        {
            return Result.Fail<ProductDetailDto>(ErrorKind.PreconditionFailed, "The product was changed by someone else; reload it and retry.");
        }
    }
}
