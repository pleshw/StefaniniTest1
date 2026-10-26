using Catalog.Domain;
using Catalog.Domain.Validation;
using FluentValidation;

namespace Catalog.Application.Products;

public sealed record DeactivateProductCommand(string Sku, string? IfMatch, string UpdatedBy);

public sealed class DeactivateProductCommandValidator : AbstractValidator<DeactivateProductCommand>
{
    public DeactivateProductCommandValidator()
    {
        RuleFor(command => command.Sku).ValidSku();
        RuleFor(command => command.IfMatch)
            .Must(ifMatch => EntityTag.TryParseVersion(ifMatch!, out _))
            .When(command => command.IfMatch is not null)
            .WithMessage("If-Match must be the ETag of a product version.");
        RuleFor(command => command.UpdatedBy).NotEmpty();
    }
}

public sealed class DeactivateProductHandler(
    IProductRepository repository,
    TimeProvider clock,
    IValidator<DeactivateProductCommand> validator)
{
    public async Task<Result<ProductDetailDto>> HandleAsync(DeactivateProductCommand command, CancellationToken cancellationToken)
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

        var stale = command.IfMatch is not null
            && EntityTag.TryParseVersion(command.IfMatch, out var ifMatchVersion)
            && ifMatchVersion != product.Version;

        return stale
            ? Result.Fail<ProductDetailDto>(ErrorKind.PreconditionFailed, $"Product {product.Sku} is at version {product.Version}.")
            : await DeactivateAsync(product, command.UpdatedBy, cancellationToken);
    }

    private async Task<Result<ProductDetailDto>> DeactivateAsync(Product product, string updatedBy, CancellationToken cancellationToken)
    {
        var expectedVersion = product.Version;
        product.Deactivate();

        if (product.Version == expectedVersion)
        {
            return Result.Ok(product.ToDetailDto());
        }

        try
        {
            await repository.SaveAsync(product, expectedVersion, new AuditStamp(updatedBy, clock.GetUtcNow()), [], cancellationToken);

            return Result.Ok(product.ToDetailDto());
        }
        catch (ConcurrencyException)
        {
            return Result.Fail<ProductDetailDto>(ErrorKind.PreconditionFailed, "The product was changed by someone else; reload it and retry.");
        }
    }
}
