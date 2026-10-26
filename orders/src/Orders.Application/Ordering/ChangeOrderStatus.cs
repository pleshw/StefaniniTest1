using FluentValidation;
using Orders.Domain;

namespace Orders.Application.Ordering;

/// <summary>What an admin does to an order.</summary>
public enum OrderAction
{
    Confirm,
    Hold,
    Block,
    Cancel,
}

/// <param name="IfMatch">The ETag of the order version the admin looked at: its row version. Required.</param>
/// <param name="Reason">Required to hold, block or cancel; optional to confirm.</param>
/// <param name="Actor">The admin's subject.</param>
public sealed record ChangeOrderStatusCommand(Guid OrderId, OrderAction Action, string? IfMatch, string? Reason, string Actor);

public sealed class ChangeOrderStatusCommandValidator : AbstractValidator<ChangeOrderStatusCommand>
{
    public const int MaxReasonLength = 500;

    public ChangeOrderStatusCommandValidator()
    {
        RuleFor(command => command.OrderId).NotEqual(Guid.Empty).WithMessage("Order id must not be empty.");
        RuleFor(command => command.Action).IsInEnum();
        RuleFor(command => command.Actor).NotEmpty();
        RuleFor(command => command.IfMatch)
            .Must(ifMatch => ifMatch is null || EntityTag.TryParseVersion(ifMatch, out _))
            .WithMessage("If-Match must be the ETag of an order version.");
        RuleFor(command => command.Reason)
            .NotEmpty()
            .When(command => command.Action is OrderAction.Hold or OrderAction.Block or OrderAction.Cancel)
            .WithMessage("A reason is required to hold, block or cancel an order.");
        RuleFor(command => command.Reason)
            .MaximumLength(MaxReasonLength)
            .WithMessage($"The reason must have at most {MaxReasonLength} characters.");
    }
}

/// <summary>
/// An admin confirms, holds, blocks or cancels an order through the order's own state machine. The row version in
/// <c>If-Match</c> must be the current one (428 without it, 412 when stale or when someone changed the order meanwhile); a
/// change the state machine refuses is an invalid transition. The status, its history row with the admin as actor, the
/// event log row and the <c>OrderStatusChanged.v1</c> outbox message are one transaction.
/// </summary>
public sealed class ChangeOrderStatusHandler(
    IOrderRepository orders,
    IOrderTransaction transaction,
    OrderStatusChangeRecorder recorder,
    TimeProvider clock,
    IValidator<ChangeOrderStatusCommand> validator)
{
    public async Task<Result<OrderDto>> HandleAsync(ChangeOrderStatusCommand command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<OrderDto>();
        }

        var order = await orders.GetByIdAsync(command.OrderId, cancellationToken);

        if (order is null)
        {
            return Result.Fail<OrderDto>(ErrorKind.NotFound, $"Order {command.OrderId} does not exist.");
        }

        if (command.IfMatch is null)
        {
            return Result.Fail<OrderDto>(ErrorKind.PreconditionRequired, "If-Match is required to change the status of an order.");
        }

        if (!EntityTag.TryParseVersion(command.IfMatch, out var version) || version != order.RowVersion)
        {
            return Result.Fail<OrderDto>(ErrorKind.PreconditionFailed, $"The order is at version {order.RowVersion}; If-Match named another version.");
        }

        try
        {
            return await transaction.ExecuteAsync(
                async token =>
                {
                    Apply(order, command);
                    await orders.UpdateAsync(order, version, token);
                    await recorder.RecordAsync(order, token);

                    return Result.Ok(order.ToDto());
                },
                cancellationToken);
        }
        catch (InvalidStatusTransitionException exception)
        {
            return Result.Fail<OrderDto>(ErrorKind.InvalidTransition, exception.Message);
        }
        catch (ConcurrencyException)
        {
            return Result.Fail<OrderDto>(ErrorKind.PreconditionFailed, "The order was changed by someone else; read it again and retry.");
        }
    }

    private void Apply(Order order, ChangeOrderStatusCommand command)
    {
        var now = clock.GetUtcNow();

        switch (command.Action)
        {
            case OrderAction.Confirm:
                order.Confirm(command.Actor, now, command.Reason);
                break;
            case OrderAction.Hold:
                order.Hold(command.Actor, command.Reason, now);
                break;
            case OrderAction.Block:
                order.Block(command.Actor, command.Reason, now);
                break;
            default:
                order.Cancel(command.Actor, command.Reason, now);
                break;
        }
    }
}
