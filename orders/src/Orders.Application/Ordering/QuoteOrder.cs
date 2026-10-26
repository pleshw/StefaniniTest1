using FluentValidation;
using Orders.Application.Pricing;
using Orders.Domain;
using Orders.Domain.Validation;

namespace Orders.Application.Ordering;

/// <summary>What an order for this cart would cost now. Nothing is stored and no idempotency key is needed.</summary>
/// <param name="Currency">ISO 4217 code; BRL when null.</param>
public sealed record QuoteOrderQuery(
    Guid ClientId,
    string ClientName,
    OrderChannel Channel,
    string? Currency,
    IReadOnlyList<CartLine> Lines);

public sealed class QuoteOrderQueryValidator : AbstractValidator<QuoteOrderQuery>
{
    public QuoteOrderQueryValidator()
    {
        RuleFor(query => query.ClientId).NotEqual(Guid.Empty).WithMessage("Client id must not be empty.");
        RuleFor(query => query.ClientName).NotEmpty().WithMessage("Client name must not be empty.");
        RuleFor(query => query.Currency).ValidCurrencyOrDefault();
    }
}

public sealed record QuoteDto(
    string Currency,
    int ItemCount,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal Total,
    IReadOnlyList<OrderItemDto> Items);

public sealed class QuoteOrderHandler(OrderDrafter drafter, IValidator<QuoteOrderQuery> validator)
{
    public async Task<Result<QuoteDto>> HandleAsync(QuoteOrderQuery query, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<QuoteDto>();
        }

        // The quoting client is also the creator: a quote never leaves this method, so who would create it does not matter.
        var draft = await drafter.DraftAsync(
            query.ClientId, query.ClientName, query.ClientId.ToString(), query.Channel, query.Currency, query.Lines, cancellationToken);

        return draft.IsSuccess
            ? Result.Ok(draft.Value.Order.ToQuoteDto())
            : Result.Fail<QuoteDto>(draft.Error!.Kind, draft.Error.Message);
    }
}
