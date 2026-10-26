using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orders.Application.Ordering;
using Orders.Domain;

namespace Orders.Infrastructure.Persistence;

internal sealed class OrderRepository(OrdersDbContext context) : IOrderRepository
{
    public async Task AddAsync(Order order, CancellationToken cancellationToken)
    {
        context.Orders.Add(order.ToRecord());

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await context.Orders
            .AsNoTracking()
            .AsSplitQuery()
            .Include(order => order.Items)
            .Include(order => order.History)
            .SingleOrDefaultAsync(order => order.Id == id, cancellationToken);

        return record is null ? null : record.ToDomain();
    }

    public async Task UpdateAsync(Order order, int expectedRowVersion, CancellationToken cancellationToken)
    {
        var record = await context.Orders
            .Include(stored => stored.History)
            .SingleOrDefaultAsync(stored => stored.Id == order.Id, cancellationToken)
            ?? throw new ConcurrencyException($"Order {order.Number} no longer exists.");

        if (record.RowVersion != expectedRowVersion)
        {
            throw new ConcurrencyException($"Order {order.Number} was changed by someone else (stored row version {record.RowVersion}, expected {expectedRowVersion}).");
        }

        var entry = context.Entry(record);
        entry.Property(stored => stored.RowVersion).OriginalValue = expectedRowVersion;
        record.Status = order.Status;
        record.UpdatedAt = order.UpdatedAt;
        record.RowVersion = order.RowVersion;

        var known = record.History.Count;
        context.OrderStatusHistory.AddRange(order.StatusHistory
            .Select((change, index) => (change, seq: index + 1))
            .Where(entryToAdd => entryToAdd.seq > known)
            .Select(entryToAdd => entryToAdd.change.ToRecord(order.Id, entryToAdd.seq)));

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException
            || exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } })
        {
            // A second writer that read the same version inserts the same (order_id, seq) history row before the row version
            // check can fail, so that primary key violation is the same conflict.
            throw new ConcurrencyException($"Order {order.Number} was changed by someone else (expected row version {expectedRowVersion}).");
        }
    }
}
