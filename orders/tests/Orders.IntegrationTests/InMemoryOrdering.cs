using System.Collections.Concurrent;
using Orders.Application;
using Orders.Application.Catalog;
using Orders.Application.Idempotency;
using Orders.Application.Ordering;
using Orders.Domain;

namespace Orders.IntegrationTests;

// In-memory stand-ins for PostgreSQL and Wolverine, so endpoint tests exercise the real HTTP pipeline and handlers
// without Docker. The Postgres-backed behaviour is covered by the Testcontainers tests.

internal sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly ConcurrentDictionary<Guid, Order> _orders = new();

    public IReadOnlyCollection<Order> All => _orders.Values.ToList();

    public Task AddAsync(Order order, CancellationToken cancellationToken)
    {
        _orders[order.Id] = order;
        return Task.CompletedTask;
    }

    public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_orders.GetValueOrDefault(id));

    public Task UpdateAsync(Order order, int expectedRowVersion, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<(Guid Client, Guid Key), IdempotencyRecord> _records = new();

    public Task<IdempotencyRecord?> TryGetAsync(Guid clientId, Guid key, CancellationToken cancellationToken) =>
        Task.FromResult(_records.GetValueOrDefault((clientId, key)));

    public Task<bool> TryReserveAsync(Guid clientId, Guid key, byte[] requestHash, Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(_records.TryAdd((clientId, key), new IdempotencyRecord(clientId, key, requestHash, 0, "{}", orderId)));

    public Task CompleteAsync(Guid clientId, Guid key, int responseStatus, string responseBody, CancellationToken cancellationToken)
    {
        _records.AddOrUpdate(
            (clientId, key),
            _ => throw new InvalidOperationException("The idempotency key was not reserved."),
            (_, current) => current with { ResponseStatus = responseStatus, ResponseBody = responseBody });
        return Task.CompletedTask;
    }
}

/// <summary>Runs one transaction at a time, as a duplicate request waiting on the primary key does in PostgreSQL.</summary>
internal sealed class SerializingOrderTransaction : IOrderTransaction, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            return await work(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

internal sealed class RecordingOrderEventPublisher : IOrderEventPublisher
{
    private readonly ConcurrentQueue<IDomainEvent> _published = new();

    public IReadOnlyCollection<IDomainEvent> Published => _published.ToList();

    public Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        _published.Enqueue(domainEvent);
        return Task.CompletedTask;
    }
}

/// <summary>A catalog with fixed products; set <see cref="Unavailable"/> to simulate an outage.</summary>
internal sealed class FakeCatalogPriceProvider : ICatalogPriceProvider
{
    private readonly Dictionary<string, (string Name, decimal Price, int Version, bool IsActive)> _products = new()
    {
        ["KB-1"] = ("Keyboard", 19.99m, 3, true),
        ["MS-1"] = ("Mouse", 10m, 1, true),
        ["OFF-1"] = ("Retired mouse", 5m, 1, false),
    };

    private int _calls;

    public bool Unavailable { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public Task<Result<CatalogPrices>> GetPricesAsync(Currency currency, IReadOnlyCollection<string> skus, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);

        if (Unavailable)
        {
            return Task.FromResult(Result.Fail<CatalogPrices>(ErrorKind.CatalogUnavailable, "The catalog could not be reached; try again shortly."));
        }

        var found = skus
            .Where(_products.ContainsKey)
            .Select(sku => new CatalogPrice(
                sku, _products[sku].Name, "peripherals", Money.Create(_products[sku].Price, currency), _products[sku].Version, _products[sku].IsActive))
            .ToList();

        return Task.FromResult(Result.Ok(new CatalogPrices(found, skus.Where(sku => !_products.ContainsKey(sku)).ToList())));
    }
}
