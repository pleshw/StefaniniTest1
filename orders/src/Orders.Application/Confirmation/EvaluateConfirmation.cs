using FluentValidation;
using Orders.Application.Ordering;
using Orders.Domain;
using Orders.Domain.Confirmation;
using Orders.Domain.Discounts;
using Orders.Domain.Rules;

namespace Orders.Application.Confirmation;

/// <summary>The active confirmation rules: the latest version of every rule, when that version is active.</summary>
public interface IConfirmationRuleRepository
{
    Task<IReadOnlyList<ConfirmationRule>> GetActiveAsync(CancellationToken cancellationToken);
}

/// <summary>What the Worker did with a placed order.</summary>
public enum ConfirmationOutcome
{
    Confirmed,

    /// <summary>A Hold rule matched; the order waits for an admin.</summary>
    Held,

    /// <summary>The order was not Pending any more (an admin acted first, or this event was handled already).</summary>
    NotPending,
}

public sealed record EvaluateConfirmationCommand(Guid OrderId);

public sealed class EvaluateConfirmationCommandValidator : AbstractValidator<EvaluateConfirmationCommand>
{
    public EvaluateConfirmationCommandValidator()
    {
        RuleFor(command => command.OrderId).NotEqual(Guid.Empty).WithMessage("Order id must not be empty.");
    }
}

/// <summary>
/// Decides a placed order: a matching Hold rule puts it OnHold (the reason is the rule name), otherwise it is Confirmed,
/// both as <c>system:worker</c> through the order's own state machine. The status, its history row, the event log row and
/// the <c>OrderStatusChanged.v1</c> outbox message are one transaction. An order that is not Pending any more is left
/// alone, which also makes a redelivered event harmless. The row version guards against an admin acting at the same time.
/// </summary>
public sealed class EvaluateConfirmationHandler(
    IOrderRepository orders,
    IOrderQueries queries,
    IConfirmationRuleRepository rules,
    IOrderTransaction transaction,
    OrderStatusChangeRecorder recorder,
    TimeProvider clock,
    IValidator<EvaluateConfirmationCommand> validator)
{
    public const string Actor = "system:worker";

    private const int _attempts = 3;

    private static readonly IReadOnlySet<string> _noSegments = new HashSet<string>();

    public async Task<Result<ConfirmationOutcome>> HandleAsync(EvaluateConfirmationCommand command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<ConfirmationOutcome>();
        }

        for (var attempt = 1; ; attempt++)
        {
            var order = await orders.GetByIdAsync(command.OrderId, cancellationToken);

            if (order is null)
            {
                return Result.Fail<ConfirmationOutcome>(ErrorKind.NotFound, $"Order {command.OrderId} does not exist.");
            }

            if (order.Status != OrderStatus.Pending)
            {
                return Result.Ok(ConfirmationOutcome.NotPending);
            }

            var hold = ConfirmationRule.FirstMatchingHold(await rules.GetActiveAsync(cancellationToken), await ContextOf(order, cancellationToken));

            try
            {
                return await transaction.ExecuteAsync(
                    async token =>
                    {
                        var expectedRowVersion = order.RowVersion;

                        if (hold is null)
                        {
                            order.Confirm(Actor, clock.GetUtcNow());
                        }
                        else
                        {
                            order.Hold(Actor, hold.Name, clock.GetUtcNow());
                        }

                        await orders.UpdateAsync(order, expectedRowVersion, token);
                        await recorder.RecordAsync(order, token);

                        return Result.Ok(hold is null ? ConfirmationOutcome.Confirmed : ConfirmationOutcome.Held);
                    },
                    cancellationToken);
            }
            catch (ConcurrencyException) when (attempt < _attempts)
            {
                // Someone changed the order since it was read: read it again and decide on what is there now.
            }
        }
    }

    /// <summary>The order as the rule conditions see it: its total after discounts, channel and whether the client ordered before.</summary>
    private async Task<RuleContext> ContextOf(Order order, CancellationToken cancellationToken)
    {
        // Orders do not store product categories; the allowed condition kinds never look at the lines.
        var cart = DiscountCart.Create(
            order.Currency, order.Items.Select(item => new DiscountLine(item.Sku, string.Empty, item.UnitPrice, item.Quantity)));
        var isFirst = !await queries.ClientHasEarlierOrdersAsync(order.Client.Id, order.Id, order.CreatedAt, cancellationToken);

        return new RuleContext(cart, order.Client.Id, _noSegments, order.Channel, isFirst, order.Total, clock.GetUtcNow());
    }
}
