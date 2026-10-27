using Microsoft.EntityFrameworkCore;
using Orders.Application.Observability;
using Orders.Domain;

namespace Orders.Infrastructure.Persistence;

/// <summary>
/// The orders waiting for a person. An order leaves OnHold only through a status change, which also sets
/// <c>updated_at</c>, so the smallest <c>updated_at</c> among the OnHold orders is when the longest wait began.
/// Served by <c>ix_orders_status_created</c> (status is its first column).
/// </summary>
internal sealed class OnHoldProbe(OrdersDbContext context) : IOnHoldProbe
{
    public async Task<OnHoldSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var held = context.Orders.AsNoTracking().Where(order => order.Status == OrderStatus.OnHold);
        var count = await held.LongCountAsync(cancellationToken);

        if (count == 0)
        {
            return new OnHoldSnapshot(0, null);
        }

        return new OnHoldSnapshot(count, await held.MinAsync(order => (DateTimeOffset?)order.UpdatedAt, cancellationToken));
    }
}
