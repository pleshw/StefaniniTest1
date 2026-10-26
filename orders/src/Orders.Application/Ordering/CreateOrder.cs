using System.Text.Json;
using FluentValidation;
using Orders.Application.Discounts;
using Orders.Application.Idempotency;
using Orders.Application.Pricing;
using Orders.Domain;
using Orders.Domain.Validation;

namespace Orders.Application.Ordering;

/// <param name="IdempotencyKey">The raw header value; the handler turns a missing or malformed one into a validation error.</param>
/// <param name="Currency">ISO 4217 code; BRL when null.</param>
/// <param name="RequestHash">Canonical hash of the request body, computed at the edge.</param>
public sealed record CreateOrderCommand(
    Guid ClientId,
    string ClientName,
    string CreatedBy,
    OrderChannel Channel,
    string? IdempotencyKey,
    string? Currency,
    IReadOnlyList<CartLine> Lines,
    byte[] RequestHash);

public sealed class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidator()
    {
        RuleFor(command => command.ClientId).NotEqual(Guid.Empty).WithMessage("Client id must not be empty.");
        RuleFor(command => command.ClientName).NotEmpty().WithMessage("Client name must not be empty.");
        RuleFor(command => command.CreatedBy).NotEmpty();
        RuleFor(command => command.Currency).ValidCurrencyOrDefault();
    }
}

/// <summary>What the endpoint sends back: the same status and body for the first request and for every replay.</summary>
public sealed record CreateOrderOutcome(int Status, string Body, Guid OrderId, bool Replayed);

/// <summary>Runs work in one database transaction; events published meanwhile leave through the outbox when it commits.</summary>
public interface IOrderTransaction
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}

public interface IOrderEventPublisher
{
    Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken);
}

public static class OrderNumbers
{
    private const int _idCharacters = 12;

    /// <summary>A readable, unique number such as ORD-2026-3F9A1C2B7D40, derived from the order id.</summary>
    public static string For(Guid orderId, DateTimeOffset now) =>
        $"ORD-{now.Year}-{orderId.ToString("N")[.._idCharacters].ToUpperInvariant()}";
}

/// <summary>The create-order flow: replay check, one price call, one transaction with the idempotency row, order and outbox event.</summary>
public sealed class CreateOrderHandler(
    IdempotencyService idempotency,
    IIdempotencyStore idempotencyStore,
    OrderDrafter drafter,
    IOrderRepository orders,
    IOrderDiscountStore discounts,
    IOrderTransaction transaction,
    IOrderEventPublisher events,
    IValidator<CreateOrderCommand> validator)
{
    private const int _createdStatus = 201;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<Result<CreateOrderOutcome>> HandleAsync(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        var key = IdempotencyKey.Parse(command.IdempotencyKey);

        if (!key.IsSuccess)
        {
            return Fail(key.Error!);
        }

        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<CreateOrderOutcome>();
        }

        var replay = await idempotency.CheckAsync(command.ClientId, key.Value, command.RequestHash, cancellationToken);

        if (!replay.IsSuccess || replay.Value is not null)
        {
            return replay.IsSuccess ? Result.Ok(ToReplay(replay.Value!)) : Fail(replay.Error!);
        }

        var draft = await drafter.DraftAsync(
            command.ClientId, command.ClientName, command.CreatedBy, command.Channel, command.Currency, command.Lines, cancellationToken);

        return draft.IsSuccess
            ? await PlaceAsync(command, key.Value, draft.Value, cancellationToken)
            : Fail(draft.Error!);
    }

    /// <summary>
    /// One transaction for the idempotency row, the order, its discounts and redemptions, and the outbox event. A
    /// once-per-client rule redeemed meanwhile rolls everything back and answers 409; the key stays unused.
    /// </summary>
    private async Task<Result<CreateOrderOutcome>> PlaceAsync(
        CreateOrderCommand command, Guid key, OrderDraft draft, CancellationToken cancellationToken)
    {
        var order = draft.Order;

        try
        {
            return await transaction.ExecuteAsync(
                async token =>
                {
                    if (!await idempotencyStore.TryReserveAsync(command.ClientId, key, command.RequestHash, order.Id, token))
                    {
                        return await ReplayWinnerAsync(command, key, token);
                    }

                    await orders.AddAsync(order, token);
                    await discounts.AddAsync(order, draft.Discounts, token);

                    foreach (var domainEvent in order.DomainEvents)
                    {
                        await events.PublishAsync(domainEvent, token);
                    }

                    order.ClearDomainEvents();

                    var body = JsonSerializer.Serialize(order.ToDto(), _json);
                    await idempotencyStore.CompleteAsync(command.ClientId, key, _createdStatus, body, token);

                    return Result.Ok(new CreateOrderOutcome(_createdStatus, body, order.Id, Replayed: false));
                },
                cancellationToken);
        }
        catch (DiscountAlreadyUsedException exception)
        {
            return Result.Fail<CreateOrderOutcome>(ErrorKind.DiscountAlreadyUsed, exception.Message);
        }
    }

    /// <summary>The reservation lost to a concurrent request with the same key that has committed meanwhile.</summary>
    private async Task<Result<CreateOrderOutcome>> ReplayWinnerAsync(CreateOrderCommand command, Guid key, CancellationToken token)
    {
        var winner = await idempotency.CheckAsync(command.ClientId, key, command.RequestHash, token);

        return winner.IsSuccess
            ? Result.Ok(ToReplay(winner.Value ?? throw new InvalidOperationException("The idempotency key is reserved but no response is stored.")))
            : Fail(winner.Error!);
    }

    private static CreateOrderOutcome ToReplay(IdempotencyRecord record) =>
        new(record.ResponseStatus, record.ResponseBody, record.OrderId, Replayed: true);

    private static Result<CreateOrderOutcome> Fail(ResultError error) => Result.Fail<CreateOrderOutcome>(error.Kind, error.Message);
}
