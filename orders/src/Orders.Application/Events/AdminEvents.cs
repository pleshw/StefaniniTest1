using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentValidation;

namespace Orders.Application.Events;

/// <summary>One row of the append-only event log: what happened to an order, as the payload that was published or recorded.</summary>
/// <param name="Id">Unique and growing with the insertion order; breaks ties between events of the same time.</param>
/// <param name="Type">A versioned name such as <c>OrderStatusChanged.v1</c> or <c>DeadLetterReplayed.v1</c>.</param>
public sealed record OrderEventDto(long Id, Guid OrderId, string Type, JsonElement Payload, DateTimeOffset OccurredAt);

public sealed record EventPage(IReadOnlyList<OrderEventDto> Items, string? Next);

/// <summary>Where the previous page stopped: events are listed oldest first, then by id.</summary>
public sealed record EventCursor(DateTimeOffset OccurredAt, long Id)
{
    public string Encode() =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(
            new[] { OccurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture), Id.ToString(CultureInfo.InvariantCulture) }));

    public static bool TryDecode(string value, out EventCursor? cursor)
    {
        cursor = null;

        try
        {
            var parts = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value)));

            if (parts is not [not null, not null]
                || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks
                || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var id))
            {
                return false;
            }

            cursor = new EventCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }
}

/// <param name="Take">How many rows to read: the page size plus one, to know whether another page exists.</param>
public sealed record EventFilter(Guid? OrderId, string? Type, EventCursor? After, int Take);

/// <summary>Read side of the event log: projections straight from storage, oldest first.</summary>
public interface IOrderEventLogQueries
{
    Task<IReadOnlyList<OrderEventDto>> ListAsync(EventFilter filter, CancellationToken cancellationToken);
}

/// <param name="After">The previous page's <c>next</c>.</param>
/// <param name="Type">Only events of this type, exactly.</param>
public sealed record ListEventsQuery(string? After, int? Limit, string? Type, Guid? OrderId);

public sealed class ListEventsQueryValidator : AbstractValidator<ListEventsQuery>
{
    public const int MaxTypeLength = 100;

    public ListEventsQueryValidator()
    {
        RuleFor(query => query.Limit)
            .InclusiveBetween(1, ListEventsHandler.MaxLimit)
            .When(query => query.Limit is not null)
            .WithMessage($"limit must be between 1 and {ListEventsHandler.MaxLimit}.");
        RuleFor(query => query.After)
            .Must(after => EventCursor.TryDecode(after!, out _))
            .When(query => query.After is not null)
            .WithMessage("after is not a valid cursor.");
        RuleFor(query => query.Type)
            .MaximumLength(MaxTypeLength)
            .WithMessage($"type must have at most {MaxTypeLength} characters.");
        RuleFor(query => query.OrderId)
            .NotEqual(Guid.Empty)
            .When(query => query.OrderId is not null)
            .WithMessage("orderId must not be empty.");
    }
}

public sealed class ListEventsHandler(IOrderEventLogQueries queries, IValidator<ListEventsQuery> validator)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    public async Task<Result<EventPage>> HandleAsync(ListEventsQuery query, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<EventPage>();
        }

        var limit = query.Limit ?? DefaultLimit;
        var filter = new EventFilter(
            query.OrderId,
            string.IsNullOrWhiteSpace(query.Type) ? null : query.Type.Trim(),
            query.After is not null && EventCursor.TryDecode(query.After, out var cursor) ? cursor : null,
            limit + 1);

        var found = await queries.ListAsync(filter, cancellationToken);
        var page = found.Take(limit).ToList();
        var next = found.Count > limit ? new EventCursor(page[^1].OccurredAt, page[^1].Id).Encode() : null;

        return Result.Ok(new EventPage(page, next));
    }
}
