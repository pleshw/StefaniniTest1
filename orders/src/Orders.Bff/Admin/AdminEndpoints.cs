using Orders.Bff.Clients;
using Orders.Bff.Clients.Orders;
using Orders.Bff.Composition;
using Orders.Bff.Events;
using Orders.Bff.Security;

namespace Orders.Bff.Admin;

/// <summary>
/// The Admin page's composed routes. The whole <c>/bff/admin</c> group, these and the pass-through routes, needs the
/// Admin role. Composed answers read their parts in parallel; a failing part is marked, not turned into a 500.
/// </summary>
internal static class AdminEndpoints
{
    public const int InspectorEvents = 50;

    private const int _eventPageSize = 100;
    private const int _maxEventPages = 20;
    private const int _onHoldQueueSize = 100;
    private const string _service = "Orders";

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/bff/admin").WithTags("Admin").RequireAuthorization(BffAuthentication.AdminPolicy);

        admin.MapGet("/orders/{id:guid}", InspectOrder)
            .WithSummary("One order with its status history and its last 50 events; a part that fails is marked, the rest is returned.");
        admin.MapGet("/orders", ListOrders)
            .WithSummary("The OnHold queue (oldest first, up to 100) and one page of orders with the given filters.");
        admin.MapPost("/orders", CreateOrder)
            .WithSummary("Creates an order for a client; Idempotency-Key is forwarded unchanged and Orders' answer returned as it is.");
        admin.MapGet("/events", ListEvents)
            .WithSummary("The order event log, oldest first (backfill for the live stream).");
        admin.MapGet("/events/stream", (HttpContext context, LiveEventStream stream, CancellationToken cancellationToken) =>
            {
                // nginx buffers proxied responses unless told not to; events must reach the page as they happen.
                context.Response.Headers["X-Accel-Buffering"] = "no";

                return TypedResults.ServerSentEvents(stream.ReadAsync(context.Request.Headers["Last-Event-ID"].ToString(), cancellationToken));
            })
            .WithSummary("Server-Sent Events of the event log, live; with Last-Event-ID the missed entries come first. Heartbeat every 15 s.");

        return endpoints;
    }

    private static async Task<IResult> InspectOrder(IOrdersApiClient orders, Guid id, CancellationToken cancellationToken)
    {
        var order = Downstream.TryAsync(_service, () => orders.GetOrderAsync(id, cancellationToken), cancellationToken);
        var history = Downstream.TryAsync(_service, () => orders.GetOrderHistoryAsync(id, cancellationToken), cancellationToken);
        var events = Downstream.TryAsync(_service, () => LastEventsAsync(orders, id, cancellationToken), cancellationToken);
        await Task.WhenAll(order, history, events);

        if (order.Result.Failure is { } failure)
        {
            return failure;
        }

        var unavailable = Missing(("history", history.Result.Failure), ("events", events.Result.Failure));

        return TypedResults.Ok(new AdminOrderView(
            order.Result.Value!, history.Result.Value?.ToList(), events.Result.Value, unavailable.Count > 0, unavailable));
    }

    /// <summary>The event log lists oldest first, so the pages are read to the end and the last 50 kept.</summary>
    private static async Task<IReadOnlyList<OrderEventDto>> LastEventsAsync(IOrdersApiClient orders, Guid id, CancellationToken cancellationToken)
    {
        var last = new Queue<OrderEventDto>(InspectorEvents);
        string? after = null;

        for (var page = 0; page < _maxEventPages; page++)
        {
            var events = await orders.ListEventsAsync(after, _eventPageSize, null, id, cancellationToken);

            foreach (var entry in events.Items)
            {
                if (last.Count == InspectorEvents)
                {
                    last.Dequeue();
                }

                last.Enqueue(entry);
            }

            if ((after = events.Next) is null)
            {
                break;
            }
        }

        return [.. last];
    }

    private static async Task<IResult> ListOrders(
        IOrdersApiClient orders, Guid? clientId, string? status, DateTimeOffset? from, DateTimeOffset? to, string? q, string? after, int? limit,
        CancellationToken cancellationToken)
    {
        var onHold = Downstream.TryAsync(
            _service, () => orders.ListOrdersAsync(null, "OnHold", null, null, null, null, _onHoldQueueSize, cancellationToken), cancellationToken);
        var page = Downstream.TryAsync(
            _service, () => orders.ListOrdersAsync(clientId, status, from, to, q, after, limit, cancellationToken), cancellationToken);
        await Task.WhenAll(onHold, page);

        if (onHold.Result.Failure is not null && page.Result.Failure is { } failure)
        {
            return failure;
        }

        var unavailable = Missing(("onHold", onHold.Result.Failure), ("orders", page.Result.Failure));
        var queue = onHold.Result.Value?.Items.Reverse().ToList();

        return TypedResults.Ok(new AdminOrderList(queue, page.Result.Value, unavailable.Count > 0, unavailable));
    }

    private static Task<IResult> CreateOrder(HttpContext context, IHttpClientFactory clients, CancellationToken cancellationToken) =>
        Downstream.ForwardAsync(
            context, clients.CreateClient(BffClients.OrdersClientName), _service, "/api/v1/admin/orders", "/api/v1/orders", "/bff/admin/orders", cancellationToken);

    private static Task<IResult> ListEvents(
        IOrdersApiClient orders, string? after, int? limit, string? type, Guid? orderId, CancellationToken cancellationToken) =>
        Downstream.CallAsync(
            _service, () => orders.ListEventsAsync(after, limit, type, orderId, cancellationToken), events => TypedResults.Ok(events), cancellationToken);

    private static List<string> Missing(params (string Name, IResult? Failure)[] parts) =>
        [.. parts.Where(part => part.Failure is not null).Select(part => part.Name)];
}
