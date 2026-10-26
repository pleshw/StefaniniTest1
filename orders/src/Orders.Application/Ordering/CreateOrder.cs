using System.Text.Json;
using FluentValidation;
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
    PricingService pricing,
    IOrderRepository orders,
    IOrderTransaction transaction,
    IOrderEventPublisher events,
    TimeProvider clock,
    IValidator<CreateOrderCommand> validator)
{
    public const string DefaultCurrency = "BRL";

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

        try
        {
            var currency = Currency.Create(command.Currency ?? DefaultCurrency);
            var client = ClientRef.Create(command.ClientId, command.ClientName);
            var priced = await pricing.PriceAsync(currency, command.Lines, cancellationToken);

            // No discounts until the discount engine arrives (C27-C29): every line keeps its full price.
            return priced.IsSuccess
                ? await PlaceAsync(command, key.Value, client, priced.Value, cancellationToken)
                : Fail(priced.Error!);
        }
        catch (DomainException exception)
        {
            return Result.Fail<CreateOrderOutcome>(ErrorKind.Validation, exception.Message);
        }
    }

    private async Task<Result<CreateOrderOutcome>> PlaceAsync(
        CreateOrderCommand command, Guid key, ClientRef client, PricedCart cart, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var orderId = Guid.NewGuid();
        var order = Order.Place(
            orderId,
            OrderNumbers.For(orderId, now),
            client,
            command.CreatedBy,
            command.Channel,
            cart.Lines.Select(line => line.ToOrderItem()),
            now);

        return await transaction.ExecuteAsync(
            async token =>
            {
                if (!await idempotencyStore.TryReserveAsync(command.ClientId, key, command.RequestHash, order.Id, token))
                {
                    return await ReplayWinnerAsync(command, key, token);
                }

                await orders.AddAsync(order, token);

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
