using System.Text.Json;
using FluentValidation;
using Orders.Domain;
using Orders.Domain.Confirmation;

namespace Orders.Application.Confirmation;

/// <summary>What an admin submits to create a confirmation rule or to replace it with a new version.</summary>
/// <param name="Action">A <see cref="ConfirmationAction"/> name; only Hold exists.</param>
/// <param name="Conditions">A JSON array of TotalAbove, FirstOrderOfClient and ChannelIs conditions, at least one.</param>
public sealed record ConfirmationRuleDefinition(string? Name, string? Action, string? Conditions, int Priority, bool IsActive);

public sealed class ConfirmationRuleDefinitionValidator : AbstractValidator<ConfirmationRuleDefinition>
{
    public ConfirmationRuleDefinitionValidator()
    {
        RuleFor(rule => rule.Name).NotEmpty().WithMessage("Rule name must not be empty.");
        RuleFor(rule => rule.Action)
            .Must(action => ConfirmationActions.TryParse(action, out _))
            .WithMessage($"action must be one of {string.Join(", ", Enum.GetNames<ConfirmationAction>())}.");
        RuleFor(rule => rule.Conditions)
            .Custom((conditions, context) =>
            {
                foreach (var error in ConfirmationRule.ValidateConditions(conditions).Errors)
                {
                    context.AddFailure("conditions", $"conditions: {error.ErrorMessage}");
                }
            });
    }
}

internal static class ConfirmationActions
{
    /// <summary>An action name as the API writes it; numbers are not accepted.</summary>
    public static bool TryParse(string? text, out ConfirmationAction action)
    {
        action = default;
        var name = Enum.GetNames<ConfirmationAction>().FirstOrDefault(candidate => string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase));

        return name is not null && Enum.TryParse(name, out action);
    }
}

internal static class ConfirmationRuleVersions
{
    /// <summary>
    /// The DTO of version <paramref name="version"/> of a rule built from <paramref name="definition"/>. The Domain builds the
    /// rule too, so what is stored is exactly what the Worker will accept when it reads it back.
    /// </summary>
    public static Result<ConfirmationRuleDto> Build(Guid id, int version, ConfirmationRuleDefinition definition, string author, DateTimeOffset now)
    {
        if (!ConfirmationActions.TryParse(definition.Action, out var action))
        {
            return Result.Fail<ConfirmationRuleDto>(ErrorKind.Validation, $"Unknown confirmation action '{definition.Action}'.");
        }

        try
        {
            var rule = ConfirmationRule.Create(id, version, definition.Name, action, definition.Conditions, definition.Priority, definition.IsActive);
            using var document = JsonDocument.Parse(definition.Conditions!);

            return Result.Ok(new ConfirmationRuleDto(
                rule.Id, rule.Version, rule.Name, rule.Action.ToString(), document.RootElement.Clone(), rule.Priority, rule.IsActive, author, now));
        }
        catch (DomainException exception)
        {
            return Result.Fail<ConfirmationRuleDto>(ErrorKind.Validation, exception.Message);
        }
    }
}

internal static class ConfirmationRulePreconditions
{
    public static ResultError? Check(string? ifMatch, int currentVersion, bool required)
    {
        if (ifMatch is null)
        {
            return required ? new ResultError(ErrorKind.PreconditionRequired, "If-Match is required to replace a confirmation rule.") : null;
        }

        return EntityTag.TryParseVersion(ifMatch, out var version) && version == currentVersion
            ? null
            : new ResultError(ErrorKind.PreconditionFailed, $"The rule is at version {currentVersion}; If-Match named another version.");
    }

    public static async Task<Result<ConfirmationRuleDto>> StoreAsync(
        IConfirmationRuleRepository repository, IActiveConfirmationRules activeRules, ConfirmationRuleDto next, CancellationToken cancellationToken)
    {
        if (!await repository.AddVersionAsync(next, cancellationToken))
        {
            return Result.Fail<ConfirmationRuleDto>(ErrorKind.PreconditionFailed, "The rule was changed by someone else; read it again and retry.");
        }

        activeRules.Invalidate();

        return Result.Ok(next);
    }
}

public sealed record CreateConfirmationRuleCommand(ConfirmationRuleDefinition Definition, string CreatedBy);

public sealed class CreateConfirmationRuleCommandValidator : AbstractValidator<CreateConfirmationRuleCommand>
{
    public CreateConfirmationRuleCommandValidator()
    {
        RuleFor(command => command.Definition).NotNull().SetValidator(new ConfirmationRuleDefinitionValidator());
        RuleFor(command => command.CreatedBy).NotEmpty();
    }
}

/// <param name="IfMatch">The ETag of the version being replaced; required.</param>
public sealed record ReplaceConfirmationRuleCommand(Guid Id, string? IfMatch, ConfirmationRuleDefinition Definition, string UpdatedBy);

public sealed class ReplaceConfirmationRuleCommandValidator : AbstractValidator<ReplaceConfirmationRuleCommand>
{
    public ReplaceConfirmationRuleCommandValidator()
    {
        RuleFor(command => command.Id).NotEqual(Guid.Empty).WithMessage("Rule id must not be empty.");
        RuleFor(command => command.IfMatch)
            .Must(ifMatch => ifMatch is null || EntityTag.TryParseVersion(ifMatch, out _))
            .WithMessage("If-Match must be the ETag of a rule version.");
        RuleFor(command => command.Definition).NotNull().SetValidator(new ConfirmationRuleDefinitionValidator());
        RuleFor(command => command.UpdatedBy).NotEmpty();
    }
}

/// <param name="IfMatch">The ETag of the version being deactivated; optional, the latest version is deactivated without it.</param>
public sealed record DeactivateConfirmationRuleCommand(Guid Id, string? IfMatch, string UpdatedBy);

public sealed class DeactivateConfirmationRuleCommandValidator : AbstractValidator<DeactivateConfirmationRuleCommand>
{
    public DeactivateConfirmationRuleCommandValidator()
    {
        RuleFor(command => command.Id).NotEqual(Guid.Empty).WithMessage("Rule id must not be empty.");
        RuleFor(command => command.IfMatch)
            .Must(ifMatch => ifMatch is null || EntityTag.TryParseVersion(ifMatch, out _))
            .WithMessage("If-Match must be the ETag of a rule version.");
        RuleFor(command => command.UpdatedBy).NotEmpty();
    }
}

/// <param name="Active">Only rules whose latest version is active (true) or inactive (false); null lists both.</param>
public sealed record ListConfirmationRulesQuery(bool? Active);

public sealed class ListConfirmationRulesQueryValidator : AbstractValidator<ListConfirmationRulesQuery>;

public sealed record GetConfirmationRuleQuery(Guid Id);

public sealed class GetConfirmationRuleQueryValidator : AbstractValidator<GetConfirmationRuleQuery>
{
    public GetConfirmationRuleQueryValidator()
    {
        RuleFor(query => query.Id).NotEqual(Guid.Empty).WithMessage("Rule id must not be empty.");
    }
}

/// <summary>Creates version 1 of a new rule. Every write empties the active-rules cache of this process.</summary>
public sealed class CreateConfirmationRuleHandler(
    IConfirmationRuleRepository repository, IActiveConfirmationRules activeRules, TimeProvider clock, IValidator<CreateConfirmationRuleCommand> validator)
{
    public async Task<Result<ConfirmationRuleDto>> HandleAsync(CreateConfirmationRuleCommand command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<ConfirmationRuleDto>();
        }

        var built = ConfirmationRuleVersions.Build(Guid.NewGuid(), 1, command.Definition, command.CreatedBy, clock.GetUtcNow());

        return built.IsSuccess
            ? await ConfirmationRulePreconditions.StoreAsync(repository, activeRules, built.Value, cancellationToken)
            : built;
    }
}

/// <summary>Stores the next version of a rule. A stale If-Match, or a concurrent edit that took the version first, is a failed precondition.</summary>
public sealed class ReplaceConfirmationRuleHandler(
    IConfirmationRuleRepository repository, IActiveConfirmationRules activeRules, TimeProvider clock, IValidator<ReplaceConfirmationRuleCommand> validator)
{
    public async Task<Result<ConfirmationRuleDto>> HandleAsync(ReplaceConfirmationRuleCommand command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<ConfirmationRuleDto>();
        }

        var current = await repository.GetLatestAsync(command.Id, cancellationToken);

        if (current is null)
        {
            return Result.Fail<ConfirmationRuleDto>(ErrorKind.NotFound, $"Confirmation rule {command.Id} does not exist.");
        }

        var precondition = ConfirmationRulePreconditions.Check(command.IfMatch, current.Version, required: true);

        if (precondition is not null)
        {
            return Result.Fail<ConfirmationRuleDto>(precondition.Kind, precondition.Message);
        }

        var built = ConfirmationRuleVersions.Build(command.Id, current.Version + 1, command.Definition, command.UpdatedBy, clock.GetUtcNow());

        return built.IsSuccess
            ? await ConfirmationRulePreconditions.StoreAsync(repository, activeRules, built.Value, cancellationToken)
            : built;
    }
}

/// <summary>Stores the next version of a rule with <c>isActive</c> false and everything else unchanged.</summary>
public sealed class DeactivateConfirmationRuleHandler(
    IConfirmationRuleRepository repository, IActiveConfirmationRules activeRules, TimeProvider clock, IValidator<DeactivateConfirmationRuleCommand> validator)
{
    public async Task<Result<ConfirmationRuleDto>> HandleAsync(DeactivateConfirmationRuleCommand command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<ConfirmationRuleDto>();
        }

        var current = await repository.GetLatestAsync(command.Id, cancellationToken);

        if (current is null)
        {
            return Result.Fail<ConfirmationRuleDto>(ErrorKind.NotFound, $"Confirmation rule {command.Id} does not exist.");
        }

        var precondition = ConfirmationRulePreconditions.Check(command.IfMatch, current.Version, required: false);

        if (precondition is not null)
        {
            return Result.Fail<ConfirmationRuleDto>(precondition.Kind, precondition.Message);
        }

        var next = current with { Version = current.Version + 1, IsActive = false, CreatedBy = command.UpdatedBy, CreatedAt = clock.GetUtcNow() };

        return await ConfirmationRulePreconditions.StoreAsync(repository, activeRules, next, cancellationToken);
    }
}

public sealed class ListConfirmationRulesHandler(IConfirmationRuleRepository repository, IValidator<ListConfirmationRulesQuery> validator)
{
    public async Task<Result<IReadOnlyList<ConfirmationRuleDto>>> HandleAsync(ListConfirmationRulesQuery query, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);

        return validation.IsValid
            ? Result.Ok(await repository.ListLatestAsync(query.Active, cancellationToken))
            : validation.ToFailure<IReadOnlyList<ConfirmationRuleDto>>();
    }
}

public sealed class GetConfirmationRuleHandler(IConfirmationRuleRepository repository, IValidator<GetConfirmationRuleQuery> validator)
{
    public async Task<Result<ConfirmationRuleDto>> HandleAsync(GetConfirmationRuleQuery query, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<ConfirmationRuleDto>();
        }

        var rule = await repository.GetLatestAsync(query.Id, cancellationToken);

        return rule is null
            ? Result.Fail<ConfirmationRuleDto>(ErrorKind.NotFound, $"Confirmation rule {query.Id} does not exist.")
            : Result.Ok(rule);
    }
}
