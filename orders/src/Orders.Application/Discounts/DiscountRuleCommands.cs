using FluentValidation;

namespace Orders.Application.Discounts;

public sealed record CreateDiscountRuleCommand(RuleDefinition Definition, string CreatedBy);

public sealed class CreateDiscountRuleCommandValidator : AbstractValidator<CreateDiscountRuleCommand>
{
    public CreateDiscountRuleCommandValidator()
    {
        RuleFor(command => command.Definition).NotNull().SetValidator(new RuleDefinitionValidator());
        RuleFor(command => command.CreatedBy).NotEmpty();
    }
}

/// <param name="IfMatch">The ETag of the version being replaced; required.</param>
public sealed record ReplaceDiscountRuleCommand(Guid Id, string? IfMatch, RuleDefinition Definition, string UpdatedBy);

public sealed class ReplaceDiscountRuleCommandValidator : AbstractValidator<ReplaceDiscountRuleCommand>
{
    public ReplaceDiscountRuleCommandValidator()
    {
        RuleFor(command => command.Id).NotEqual(Guid.Empty).WithMessage("Rule id must not be empty.");
        RuleFor(command => command.IfMatch).ValidEntityTag();
        RuleFor(command => command.Definition).NotNull().SetValidator(new RuleDefinitionValidator());
        RuleFor(command => command.UpdatedBy).NotEmpty();
    }
}

/// <param name="IfMatch">The ETag of the version being deactivated; optional, the latest version is deactivated without it.</param>
public sealed record DeactivateDiscountRuleCommand(Guid Id, string? IfMatch, string UpdatedBy);

public sealed class DeactivateDiscountRuleCommandValidator : AbstractValidator<DeactivateDiscountRuleCommand>
{
    public DeactivateDiscountRuleCommandValidator()
    {
        RuleFor(command => command.Id).NotEqual(Guid.Empty).WithMessage("Rule id must not be empty.");
        RuleFor(command => command.IfMatch).ValidEntityTag();
        RuleFor(command => command.UpdatedBy).NotEmpty();
    }
}

internal static class EntityTagRules
{
    public static IRuleBuilderOptions<T, string?> ValidEntityTag<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(ifMatch => ifMatch is null || EntityTag.TryParseVersion(ifMatch, out _))
            .WithMessage("If-Match must be the ETag of a rule version.");
}

/// <summary>Creates version 1 of a new rule. Every write to the rules empties the active-rules cache.</summary>
public sealed class CreateDiscountRuleHandler(
    IDiscountRuleRepository repository, IActiveDiscountRules activeRules, TimeProvider clock, IValidator<CreateDiscountRuleCommand> validator)
{
    public async Task<Result<DiscountRuleDto>> HandleAsync(CreateDiscountRuleCommand command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<DiscountRuleDto>();
        }

        var built = DiscountRuleVersions.Build(Guid.NewGuid(), 1, command.Definition, command.CreatedBy, clock.GetUtcNow());

        if (!built.IsSuccess)
        {
            return built;
        }

        await repository.AddVersionAsync(built.Value, cancellationToken);
        activeRules.Invalidate();

        return built;
    }
}

/// <summary>Stores the next version of a rule. A stale If-Match, or a concurrent edit that took the version first, is a failed precondition.</summary>
public sealed class ReplaceDiscountRuleHandler(
    IDiscountRuleRepository repository, IActiveDiscountRules activeRules, TimeProvider clock, IValidator<ReplaceDiscountRuleCommand> validator)
{
    public async Task<Result<DiscountRuleDto>> HandleAsync(ReplaceDiscountRuleCommand command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<DiscountRuleDto>();
        }

        var current = await repository.GetLatestAsync(command.Id, cancellationToken);

        if (current is null)
        {
            return Result.Fail<DiscountRuleDto>(ErrorKind.NotFound, $"Discount rule {command.Id} does not exist.");
        }

        var precondition = DiscountRulePreconditions.Check(command.IfMatch, current.Version, required: true);

        if (precondition is not null)
        {
            return Result.Fail<DiscountRuleDto>(precondition.Kind, precondition.Message);
        }

        var built = DiscountRuleVersions.Build(command.Id, current.Version + 1, command.Definition, command.UpdatedBy, clock.GetUtcNow());

        return built.IsSuccess ? await DiscountRuleWrites.StoreAsync(repository, activeRules, built.Value, cancellationToken) : built;
    }
}

/// <summary>Stores the next version of a rule with <c>isActive</c> false and everything else unchanged.</summary>
public sealed class DeactivateDiscountRuleHandler(
    IDiscountRuleRepository repository, IActiveDiscountRules activeRules, TimeProvider clock, IValidator<DeactivateDiscountRuleCommand> validator)
{
    public async Task<Result<DiscountRuleDto>> HandleAsync(DeactivateDiscountRuleCommand command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<DiscountRuleDto>();
        }

        var current = await repository.GetLatestAsync(command.Id, cancellationToken);

        if (current is null)
        {
            return Result.Fail<DiscountRuleDto>(ErrorKind.NotFound, $"Discount rule {command.Id} does not exist.");
        }

        var precondition = DiscountRulePreconditions.Check(command.IfMatch, current.Version, required: false);

        if (precondition is not null)
        {
            return Result.Fail<DiscountRuleDto>(precondition.Kind, precondition.Message);
        }

        var next = current with { Version = current.Version + 1, IsActive = false, CreatedBy = command.UpdatedBy, CreatedAt = clock.GetUtcNow() };

        return await DiscountRuleWrites.StoreAsync(repository, activeRules, next, cancellationToken);
    }
}

internal static class DiscountRulePreconditions
{
    public static ResultError? Check(string? ifMatch, int currentVersion, bool required)
    {
        if (ifMatch is null)
        {
            return required ? new ResultError(ErrorKind.PreconditionRequired, "If-Match is required to replace a discount rule.") : null;
        }

        return EntityTag.TryParseVersion(ifMatch, out var version) && version == currentVersion
            ? null
            : new ResultError(ErrorKind.PreconditionFailed, $"The rule is at version {currentVersion}; If-Match named another version.");
    }
}

internal static class DiscountRuleWrites
{
    public static async Task<Result<DiscountRuleDto>> StoreAsync(
        IDiscountRuleRepository repository, IActiveDiscountRules activeRules, DiscountRuleDto next, CancellationToken cancellationToken)
    {
        if (!await repository.AddVersionAsync(next, cancellationToken))
        {
            return Result.Fail<DiscountRuleDto>(ErrorKind.PreconditionFailed, "The rule was changed by someone else; read it again and retry.");
        }

        activeRules.Invalidate();

        return Result.Ok(next);
    }
}
