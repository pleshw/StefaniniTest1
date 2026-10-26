using Orders.Domain;

namespace Orders.Application.Ordering;

public interface IOrderRepository
{
    Task AddAsync(Order order, CancellationToken cancellationToken);

    /// <summary>
    /// Serializes the orders of one client: blocks until no other open transaction holds the client's lock, then holds
    /// it until the surrounding transaction ends. Call it inside <see cref="IOrderTransaction"/>.
    /// </summary>
    Task LockClientAsync(Guid clientId, CancellationToken cancellationToken);

    Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the status changes of an order loaded earlier. Succeeds only while the stored row version still equals
    /// <paramref name="expectedRowVersion"/>; otherwise throws <see cref="ConcurrencyException"/>.
    /// </summary>
    Task UpdateAsync(Order order, int expectedRowVersion, CancellationToken cancellationToken);
}

public sealed class ConcurrencyException(string message) : Exception(message);
