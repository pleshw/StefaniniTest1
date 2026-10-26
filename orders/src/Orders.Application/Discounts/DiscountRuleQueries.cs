using FluentValidation;
using Orders.Application.Ordering;
using Orders.Application.Pricing;
using Orders.Domain;
using Orders.Domain.Discounts;
using Orders.Domain.Validation;

namespace Orders.Application.Discounts;

/// <param name="Active">Only rules whose latest version is active (true) or inactive (false); null lists both.</param>
public sealed record ListDiscountRulesQuery(bool? Active);

public sealed class ListDiscountRulesQueryValidator : AbstractValidator<ListDiscountRulesQuery>;

public sealed record GetDiscountRuleQuery(Guid Id);

public sealed class GetDiscountRuleQueryValidator : AbstractValidator<GetDiscountRuleQuery>
{
    public GetDiscountRuleQueryValidator()
    {
        RuleFor(query => query.Id).NotEqual(Guid.Empty).WithMessage("Rule id must not be empty.");
    }
}

public sealed class ListDiscountRulesHandler(IDiscountRuleRepository repository, IValidator<ListDiscountRulesQuery> validator)
{
    public async Task<Result<IReadOnlyList<DiscountRuleDto>>> HandleAsync(ListDiscountRulesQuery query, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);

        return validation.IsValid
            ? Result.Ok(await repository.ListLatestAsync(query.Active, cancellationToken))
            : validation.ToFailure<IReadOnlyList<DiscountRuleDto>>();
    }
}

public sealed class GetDiscountRuleHandler(IDiscountRuleRepository repository, IValidator<GetDiscountRuleQuery> validator)
{
    public async Task<Result<DiscountRuleDto>> HandleAsync(GetDiscountRuleQuery query, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<DiscountRuleDto>();
        }

        var rule = await repository.GetLatestAsync(query.Id, cancellationToken);

        return rule is null
            ? Result.Fail<DiscountRuleDto>(ErrorKind.NotFound, $"Discount rule {query.Id} does not exist.")
            : Result.Ok(rule);
    }
}

/// <summary>A sample cart priced like a quote, optionally with a rule that is not stored yet.</summary>
/// <param name="ClientId">The sample client; a client with no orders when null, so first-order rules apply.</param>
/// <param name="Rule">A rule to try on top of the active ones; null previews the active rules alone.</param>
/// <param name="RuleId">With <paramref name="Rule"/>: the stored rule it would replace, whose active version is left out of the preview.</param>
public sealed record PreviewDiscountRuleQuery(
    Guid? ClientId,
    string? Currency,
    IReadOnlyList<CartLine> Lines,
    RuleDefinition? Rule,
    Guid? RuleId = null);

public sealed class PreviewDiscountRuleQueryValidator : AbstractValidator<PreviewDiscountRuleQuery>
{
    public PreviewDiscountRuleQueryValidator()
    {
        RuleFor(query => query.Currency).ValidCurrencyOrDefault();
        RuleFor(query => query.Rule!).SetValidator(new RuleDefinitionValidator()).When(query => query.Rule is not null);
    }
}

public sealed record AppliedDiscountDto(Guid RuleId, int RuleVersion, decimal Amount);

public sealed record PreviewDto(QuoteDto Quote, IReadOnlyList<AppliedDiscountDto> Applied);

/// <summary>
/// Previews through <see cref="OrderDrafter"/>, the path create and quote use, over the active rules plus the candidate,
/// so a preview without a candidate equals the quote of the same cart. Nothing is stored and the cache is untouched.
/// </summary>
public sealed class PreviewDiscountRuleHandler(
    PricingService pricing,
    IActiveDiscountRules activeRules,
    IOrderQueries orders,
    DiscountEngine engine,
    TimeProvider clock,
    IValidator<PreviewDiscountRuleQuery> validator)
{
    private const string _previewClient = "Preview";

    public async Task<Result<PreviewDto>> HandleAsync(PreviewDiscountRuleQuery query, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<PreviewDto>();
        }

        var candidate = query.Rule is null ? null : CandidateOf(query);

        if (candidate is { IsSuccess: false })
        {
            return Result.Fail<PreviewDto>(candidate.Error!.Kind, candidate.Error.Message);
        }

        var drafter = new OrderDrafter(
            pricing, new PreviewRules(activeRules, candidate?.Value, query.RuleId ?? candidate?.Value.Id), orders, engine, clock);
        var clientId = query.ClientId ?? Guid.NewGuid();
        var draft = await drafter.DraftAsync(clientId, _previewClient, clientId.ToString(), OrderChannel.Shop, query.Currency, query.Lines, cancellationToken);

        return draft.IsSuccess
            ? Result.Ok(new PreviewDto(
                draft.Value.Order.ToQuoteDto(),
                draft.Value.Discounts.Select(applied => new AppliedDiscountDto(applied.RuleId, applied.RuleVersion, applied.Amount.Amount)).ToList()))
            : Result.Fail<PreviewDto>(draft.Error!.Kind, draft.Error.Message);
    }

    private Result<DiscountRule> CandidateOf(PreviewDiscountRuleQuery query)
    {
        var built = DiscountRuleVersions.Build(query.RuleId ?? Guid.NewGuid(), 1, query.Rule!, _previewClient, clock.GetUtcNow());

        if (!built.IsSuccess)
        {
            return Result.Fail<DiscountRule>(built.Error!.Kind, built.Error.Message);
        }

        var dto = built.Value;

        return Result.Ok(DiscountRule.Create(
            dto.Id, dto.Version, dto.Name, Enum.Parse<DiscountType>(dto.Type), dto.Parameters.GetRawText(), dto.Conditions.GetRawText(),
            dto.Priority, dto.Stackable, dto.OncePerClient, dto.ValidFrom, dto.ValidTo, dto.IsActive));
    }

    /// <summary>The active rules with the candidate added, replacing the stored rule it stands for; not cached, never invalidated.</summary>
    private sealed class PreviewRules(IActiveDiscountRules active, DiscountRule? candidate, Guid? replaces) : IActiveDiscountRules
    {
        public async Task<IReadOnlyList<DiscountRule>> GetAsync(Currency currency, CancellationToken cancellationToken)
        {
            var rules = (await active.GetAsync(currency, cancellationToken)).Where(rule => rule.Id != replaces);

            return candidate is not null && candidate.CanApplyIn(currency)
                ? rules.Append(candidate).OrderBy(rule => rule.Priority).ThenBy(rule => rule.Id).ToList()
                : rules.ToList();
        }

        public void Invalidate()
        {
        }
    }
}
