using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentValidation;
using Orders.Domain;

namespace Orders.Application.Ordering;

public sealed record OrderSummaryDto(
    Guid Id,
    string Number,
    ClientDto Client,
    string Status,
    string Channel,
    string Currency,
    int ItemCount,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal Total,
    DateTimeOffset CreatedAt);

public sealed record OrderPage(IReadOnlyList<OrderSummaryDto> Items, string? Next);

/// <param name="From">Null for the first entry, written when the order was placed.</param>
public sealed record OrderStatusChangeDto(int Seq, string? From, string To, string? Reason, string Actor, DateTimeOffset ChangedAt);

/// <summary>Where the previous page stopped: orders are listed newest first, then by id.</summary>
public sealed record OrderCursor(DateTimeOffset CreatedAt, Guid Id)
{
    public string Encode() =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new[] { CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), Id.ToString() }));

    public static bool TryDecode(string value, out OrderCursor? cursor)
    {
        cursor = null;

        try
        {
            var parts = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value)));

            if (parts is not [not null, not null]
                || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks
                || !Guid.TryParse(parts[1], out var id))
            {
                return false;
            }

            cursor = new OrderCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }
}

/// <param name="ClientId">Null lists every client (admins only).</param>
/// <param name="From">Inclusive lower bound on the creation time, in UTC.</param>
/// <param name="To">Exclusive upper bound on the creation time, in UTC.</param>
/// <param name="Search">Full text over number and client name, plus partial client names.</param>
/// <param name="Take">How many rows to read: the page size plus one, to know whether another page exists.</param>
public sealed record OrderListFilter(
    Guid? ClientId, OrderStatus? Status, DateTimeOffset? From, DateTimeOffset? To, string? Search, OrderCursor? After, int Take);

/// <summary>Read side of orders: projections straight from storage, never tracked.</summary>
public interface IOrderQueries
{
    Task<IReadOnlyList<OrderSummaryDto>> ListAsync(OrderListFilter filter, CancellationToken cancellationToken);

    /// <param name="ownerId">When set, an order of another client is reported as missing.</param>
    Task<OrderDto?> GetAsync(Guid id, Guid? ownerId, CancellationToken cancellationToken);

    /// <param name="ownerId">When set, an order of another client is reported as missing.</param>
    Task<IReadOnlyList<OrderStatusChangeDto>?> GetHistoryAsync(Guid id, Guid? ownerId, CancellationToken cancellationToken);

    /// <summary>Whether the client placed any order before; feeds the <c>FirstOrderOfClient</c> condition.</summary>
    Task<bool> ClientHasOrdersAsync(Guid clientId, CancellationToken cancellationToken);

    /// <summary>Whether the client has an order other than <paramref name="orderId"/> placed before <paramref name="placedAt"/> (ties broken by id).</summary>
    Task<bool> ClientHasEarlierOrdersAsync(Guid clientId, Guid orderId, DateTimeOffset placedAt, CancellationToken cancellationToken);
}

/// <param name="ViewerClientId">The signed-in shopper, who only sees their own orders; null for an admin.</param>
/// <param name="Status">An <see cref="OrderStatus"/> name, case-insensitive.</param>
public sealed record ListOrdersQuery(
    Guid? ViewerClientId,
    Guid? ClientId,
    string? Status,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Q,
    string? After,
    int? Limit);

/// <param name="ViewerClientId">The signed-in shopper, who only sees their own orders; null for an admin.</param>
public sealed record GetOrderQuery(Guid Id, Guid? ViewerClientId);

/// <param name="ViewerClientId">The signed-in shopper, who only sees their own orders; null for an admin.</param>
public sealed record GetOrderHistoryQuery(Guid Id, Guid? ViewerClientId);

public sealed class ListOrdersQueryValidator : AbstractValidator<ListOrdersQuery>
{
    public const int MaxSearchLength = 200;

    public ListOrdersQueryValidator()
    {
        RuleFor(query => query.Limit)
            .InclusiveBetween(1, ListOrdersHandler.MaxLimit)
            .WithMessage($"limit must be between 1 and {ListOrdersHandler.MaxLimit}.");
        RuleFor(query => query.Status)
            .Must(status => ListOrdersHandler.TryParseStatus(status, out _))
            .When(query => query.Status is not null)
            .WithMessage($"status must be one of {string.Join(", ", Enum.GetNames<OrderStatus>())}.");
        RuleFor(query => query.To)
            .GreaterThan(query => query.From)
            .When(query => query.From is not null && query.To is not null)
            .WithMessage("to must be later than from.");
        RuleFor(query => query.Q)
            .MaximumLength(MaxSearchLength)
            .WithMessage($"q must have at most {MaxSearchLength} characters.");
        RuleFor(query => query.After)
            .Must(after => OrderCursor.TryDecode(after!, out _))
            .When(query => query.After is not null)
            .WithMessage("after is not a valid cursor.");
    }
}

public sealed class GetOrderQueryValidator : AbstractValidator<GetOrderQuery>
{
    public GetOrderQueryValidator()
    {
        RuleFor(query => query.Id).NotEqual(Guid.Empty).WithMessage("Order id must not be empty.");
    }
}

public sealed class GetOrderHistoryQueryValidator : AbstractValidator<GetOrderHistoryQuery>
{
    public GetOrderHistoryQueryValidator()
    {
        RuleFor(query => query.Id).NotEqual(Guid.Empty).WithMessage("Order id must not be empty.");
    }
}

public sealed class ListOrdersHandler(IOrderQueries queries, IValidator<ListOrdersQuery> validator)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public async Task<Result<OrderPage>> HandleAsync(ListOrdersQuery query, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<OrderPage>();
        }

        if (query.ViewerClientId is not null && query.ClientId is not null && query.ClientId != query.ViewerClientId)
        {
            return Result.Fail<OrderPage>(ErrorKind.Forbidden, "Shoppers can only list their own orders.");
        }

        var limit = query.Limit ?? DefaultLimit;
        var filter = new OrderListFilter(
            query.ViewerClientId ?? query.ClientId,
            TryParseStatus(query.Status, out var status) ? status : null,
            query.From?.ToUniversalTime(),
            query.To?.ToUniversalTime(),
            string.IsNullOrWhiteSpace(query.Q) ? null : query.Q.Trim(),
            query.After is not null && OrderCursor.TryDecode(query.After, out var cursor) ? cursor : null,
            limit + 1);

        var found = await queries.ListAsync(filter, cancellationToken);
        var page = found.Take(limit).ToList();
        var next = found.Count > limit ? new OrderCursor(page[^1].CreatedAt, page[^1].Id).Encode() : null;

        return Result.Ok(new OrderPage(page, next));
    }

    /// <summary>Accepts a status name in any case; numbers are not status names.</summary>
    internal static bool TryParseStatus(string? value, out OrderStatus? status)
    {
        status = null;
        var name = Enum.GetNames<OrderStatus>().FirstOrDefault(name => string.Equals(name, value?.Trim(), StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            return false;
        }

        status = Enum.Parse<OrderStatus>(name);
        return true;
    }
}

public sealed class GetOrderHandler(IOrderQueries queries, IValidator<GetOrderQuery> validator)
{
    public async Task<Result<OrderDto>> HandleAsync(GetOrderQuery query, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<OrderDto>();
        }

        var order = await queries.GetAsync(query.Id, query.ViewerClientId, cancellationToken);

        return order is null ? OrderNotFound<OrderDto>(query.Id) : Result.Ok(order);
    }

    internal static Result<T> OrderNotFound<T>(Guid id) => Result.Fail<T>(ErrorKind.NotFound, $"Order {id} was not found.");
}

public sealed class GetOrderHistoryHandler(IOrderQueries queries, IValidator<GetOrderHistoryQuery> validator)
{
    public async Task<Result<IReadOnlyList<OrderStatusChangeDto>>> HandleAsync(GetOrderHistoryQuery query, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToFailure<IReadOnlyList<OrderStatusChangeDto>>();
        }

        var history = await queries.GetHistoryAsync(query.Id, query.ViewerClientId, cancellationToken);

        return history is null ? GetOrderHandler.OrderNotFound<IReadOnlyList<OrderStatusChangeDto>>(query.Id) : Result.Ok(history);
    }
}
