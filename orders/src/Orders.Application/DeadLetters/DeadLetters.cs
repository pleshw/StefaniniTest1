using System.Text.Json;
using FluentValidation;
using Orders.Application.Ordering;

namespace Orders.Application.DeadLetters;

/// <summary>A message parked in the dead letter queue, with what is known about why.</summary>
/// <param name="MessageId">The AMQP message id; the key for replaying.</param>
/// <param name="MessageType">The message type name, such as Orders.Contracts.OrderPlacedV1.</param>
/// <param name="Payload">The message body as JSON text.</param>
/// <param name="Headers">Every header of the parked message as text.</param>
public sealed record DeadLetterDto(
    string MessageId,
    string? MessageType,
    string? OriginalDestination,
    string? ExceptionType,
    string? ExceptionMessage,
    int? Attempts,
    string Payload,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>What a replay did: the message went to <paramref name="Exchange"/> with <paramref name="RoutingKey"/> under a new message id.</summary>
public sealed record DeadLetterReplay(DeadLetterDto Letter, string NewMessageId, string Exchange, string RoutingKey, string ReplayedBy);

/// <param name="Check">Looks at the message before anything is published; an error leaves it in the queue untouched.</param>
/// <param name="Record">Runs after the message was republished and before it is removed from the queue; if it throws, the message stays.</param>
public sealed record DeadLetterReplayHooks(
    Func<DeadLetterDto, CancellationToken, Task<ResultError?>> Check,
    Func<DeadLetterReplay, CancellationToken, Task> Record);

/// <summary>The dead letter queue <c>orders.dlq</c>: look at what failed for good, and send a message back.</summary>
public interface IDeadLetters
{
    /// <summary>Up to <paramref name="limit"/> messages, oldest first. They stay in the queue.</summary>
    Task<IReadOnlyList<DeadLetterDto>> PeekAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the message with this id, republishes it to the exchange it came through with an <c>x-replayed-by</c> header and
    /// removes it from the queue. NotFound when no message has that id; the hooks can veto and record.
    /// </summary>
    Task<Result<DeadLetterReplay>> ReplayAsync(string messageId, string replayedBy, DeadLetterReplayHooks hooks, CancellationToken cancellationToken);
}

/// <summary>Raised by an <see cref="IDeadLetters"/> adapter when the message broker cannot be reached.</summary>
public sealed class MessagingUnavailableException(string message, Exception innerException) : Exception(message, innerException);

/// <param name="Limit">How many messages to look at, 1 to 100; 100 when null.</param>
public sealed record ListDeadLettersQuery(int? Limit);

public sealed class ListDeadLettersQueryValidator : AbstractValidator<ListDeadLettersQuery>
{
    public const int MaxLimit = 100;

    public ListDeadLettersQueryValidator()
    {
        RuleFor(query => query.Limit).InclusiveBetween(1, MaxLimit).When(query => query.Limit is not null)
            .WithMessage($"limit must be between 1 and {MaxLimit}.");
    }
}

public sealed class ListDeadLettersHandler(IDeadLetters queue, IValidator<ListDeadLettersQuery> validator)
{
    public async Task<Result<IReadOnlyList<DeadLetterDto>>> HandleAsync(ListDeadLettersQuery query, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<IReadOnlyList<DeadLetterDto>>();
        }

        try
        {
            return Result.Ok(await queue.PeekAsync(query.Limit ?? ListDeadLettersQueryValidator.MaxLimit, cancellationToken));
        }
        catch (MessagingUnavailableException exception)
        {
            return Result.Fail<IReadOnlyList<DeadLetterDto>>(ErrorKind.MessagingUnavailable, exception.Message);
        }
    }
}

public sealed record ReplayDeadLetterCommand(string MessageId, string Actor);

public sealed class ReplayDeadLetterCommandValidator : AbstractValidator<ReplayDeadLetterCommand>
{
    public ReplayDeadLetterCommandValidator()
    {
        RuleFor(command => command.MessageId).NotEmpty().MaximumLength(200).WithMessage("A message id is required.");
        RuleFor(command => command.Actor).NotEmpty();
    }
}

/// <summary>
/// Sends a dead-lettered message back through its exchange. Only messages about an existing order are replayed, so the
/// replay can be written to <c>order_event_log</c> for that order, with who replayed it.
/// </summary>
public sealed class ReplayDeadLetterHandler(
    IDeadLetters queue, IOrderQueries orders, IOrderEventLog eventLog, TimeProvider clock, IValidator<ReplayDeadLetterCommand> validator)
{
    public const string ReplayedType = "DeadLetterReplayed.v1";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<Result<DeadLetterReplay>> HandleAsync(ReplayDeadLetterCommand command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<DeadLetterReplay>();
        }

        try
        {
            return await ReplayAsync(command, cancellationToken);
        }
        catch (MessagingUnavailableException exception)
        {
            return Result.Fail<DeadLetterReplay>(ErrorKind.MessagingUnavailable, exception.Message);
        }
    }

    private async Task<Result<DeadLetterReplay>> ReplayAsync(ReplayDeadLetterCommand command, CancellationToken cancellationToken) =>
        await queue.ReplayAsync(
            command.MessageId,
            command.Actor,
            new DeadLetterReplayHooks(
                async (letter, token) =>
                {
                    var orderId = OrderIdOf(letter);

                    if (orderId is null)
                    {
                        return new ResultError(ErrorKind.Unprocessable, "The message does not name an order, so it cannot be replayed.");
                    }

                    return await orders.GetAsync(orderId.Value, null, token) is null
                        ? new ResultError(ErrorKind.Unprocessable, $"Order {orderId} does not exist, so the message cannot be replayed.")
                        : null;
                },
                async (replay, token) => await eventLog.AppendEntryAsync(
                    new OrderEventLogEntry(OrderIdOf(replay.Letter)!.Value, ReplayedType, Describe(replay), clock.GetUtcNow()), token)),
            cancellationToken);

    private static Guid? OrderIdOf(DeadLetterDto letter)
    {
        try
        {
            using var document = JsonDocument.Parse(letter.Payload);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("orderId", out var id)
                && id.ValueKind == JsonValueKind.String
                && Guid.TryParse(id.GetString(), out var orderId)
                    ? orderId
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Describe(DeadLetterReplay replay) => JsonSerializer.Serialize(
        new
        {
            messageId = replay.Letter.MessageId,
            newMessageId = replay.NewMessageId,
            exchange = replay.Exchange,
            routingKey = replay.RoutingKey,
            replayedBy = replay.ReplayedBy,
            exceptionType = replay.Letter.ExceptionType,
            exceptionMessage = replay.Letter.ExceptionMessage,
        },
        _json);
}
