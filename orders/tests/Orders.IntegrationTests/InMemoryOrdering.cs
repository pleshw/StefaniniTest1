using System.Collections.Concurrent;
using Orders.Application;
using Orders.Application.Catalog;
using Orders.Application.Confirmation;
using Orders.Application.Discounts;
using Orders.Application.Idempotency;
using Orders.Application.Ordering;
using Orders.Domain;
using Orders.Domain.Confirmation;
using Orders.Domain.Discounts;

namespace Orders.IntegrationTests;

// In-memory stand-ins for PostgreSQL and Wolverine, so endpoint tests exercise the real HTTP pipeline and handlers
// without Docker. The Postgres-backed behaviour is covered by the Testcontainers tests.

internal sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Order> _orders = [];

    /// <summary>The stored orders. Like a database row, a stored order changes only through <see cref="UpdateAsync"/>.</summary>
    public IReadOnlyCollection<Order> All
    {
        get
        {
            lock (_gate)
            {
                return _orders.Values.ToList();
            }
        }
    }

    public Task AddAsync(Order order, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _orders[order.Id] = Stored(order);
        }

        return Task.CompletedTask;
    }

    /// <summary>A copy, as a query would materialize it: changing it does not change the stored order.</summary>
    public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_orders.TryGetValue(id, out var order) ? Stored(order) : null);
        }
    }

    public Task UpdateAsync(Order order, int expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_orders.TryGetValue(order.Id, out var current) || current.RowVersion != expectedRowVersion)
            {
                throw new ConcurrencyException($"Order {order.Number} was changed by someone else (expected row version {expectedRowVersion}).");
            }

            _orders[order.Id] = Stored(order);
        }

        return Task.CompletedTask;
    }

    private static Order Stored(Order order) => Order.Restore(
        order.Id, order.Number, order.Client, order.CreatedBy, order.Channel, order.Status, order.Items, order.StatusHistory,
        order.CreatedAt, order.UpdatedAt, order.RowVersion);
}

internal sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<(Guid Client, Guid Key), IdempotencyRecord> _records = new();

    /// <summary>A reserved key is invisible until it is completed, as an uncommitted row is in PostgreSQL.</summary>
    public Task<IdempotencyRecord?> TryGetAsync(Guid clientId, Guid key, CancellationToken cancellationToken) =>
        Task.FromResult(_records.GetValueOrDefault((clientId, key)) is { ResponseStatus: not 0 } completed ? completed : null);

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

/// <summary>Order reads over <see cref="InMemoryOrderRepository"/>, ordered and paged like the SQL queries.</summary>
internal sealed class InMemoryOrderQueries(InMemoryOrderRepository orders) : IOrderQueries
{
    public Task<IReadOnlyList<OrderSummaryDto>> ListAsync(OrderListFilter filter, CancellationToken cancellationToken)
    {
        var found = orders.All
            .Where(order => filter.ClientId is null || order.Client.Id == filter.ClientId)
            .Where(order => filter.Status is null || order.Status == filter.Status)
            .Where(order => filter.From is null || order.CreatedAt >= filter.From)
            .Where(order => filter.To is null || order.CreatedAt < filter.To)
            .Where(order => filter.Search is null
                || order.Number.Contains(filter.Search, StringComparison.OrdinalIgnoreCase)
                || order.Client.Name.Contains(filter.Search, StringComparison.OrdinalIgnoreCase))
            .Where(order => filter.After is null
                || order.CreatedAt < filter.After.CreatedAt
                || (order.CreatedAt == filter.After.CreatedAt && order.Id.CompareTo(filter.After.Id) > 0))
            .OrderByDescending(order => order.CreatedAt)
            .ThenBy(order => order.Id)
            .Take(filter.Take)
            .Select(Summary)
            .ToList();

        return Task.FromResult<IReadOnlyList<OrderSummaryDto>>(found);
    }

    public Task<OrderDto?> GetAsync(Guid id, Guid? ownerId, CancellationToken cancellationToken) =>
        Task.FromResult(Visible(id, ownerId)?.ToDto());

    public Task<IReadOnlyList<OrderStatusChangeDto>?> GetHistoryAsync(Guid id, Guid? ownerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OrderStatusChangeDto>?>(Visible(id, ownerId)?.StatusHistory
            .Select((change, index) => new OrderStatusChangeDto(index + 1, change.From?.ToString(), change.To.ToString(), change.Reason, change.Actor, change.ChangedAt))
            .ToList());

    public Task<bool> ClientHasOrdersAsync(Guid clientId, CancellationToken cancellationToken) =>
        Task.FromResult(orders.All.Any(order => order.Client.Id == clientId));

    public Task<bool> ClientHasEarlierOrdersAsync(Guid clientId, Guid orderId, DateTimeOffset placedAt, CancellationToken cancellationToken) =>
        Task.FromResult(orders.All.Any(order => order.Client.Id == clientId && order.Id != orderId
            && (order.CreatedAt < placedAt || (order.CreatedAt == placedAt && order.Id.CompareTo(orderId) < 0))));

    private Order? Visible(Guid id, Guid? ownerId) =>
        orders.All.SingleOrDefault(order => order.Id == id && (ownerId is null || order.Client.Id == ownerId));

    private static OrderSummaryDto Summary(Order order)
    {
        var dto = order.ToDto();

        return new OrderSummaryDto(
            dto.Id, dto.Number, dto.Client, dto.Status, dto.Channel, dto.Currency, dto.ItemCount, dto.Subtotal, dto.DiscountTotal, dto.Total, dto.CreatedAt);
    }
}

/// <summary>Active rules set by the test; filtered per currency like the cached repository.</summary>
internal sealed class InMemoryActiveDiscountRules : IActiveDiscountRules
{
    public List<DiscountRule> Rules { get; } = [];

    public Task<IReadOnlyList<DiscountRule>> GetAsync(Currency currency, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiscountRule>>(Rules.Where(rule => rule.CanApplyIn(currency)).ToList());

    public int Invalidations { get; private set; }

    public void Invalidate() => Invalidations++;
}

/// <summary>Rule versions kept in a list, with the same "latest version wins" reading as the SQL repository.</summary>
internal sealed class InMemoryDiscountRuleRepository : IDiscountRuleRepository
{
    private readonly List<DiscountRuleDto> _versions = [];

    public Task<IReadOnlyList<DiscountRule>> GetActiveAsync(CancellationToken cancellationToken)
    {
        lock (_versions)
        {
            return Task.FromResult<IReadOnlyList<DiscountRule>>(Latest().Where(rule => rule.IsActive).OrderBy(rule => rule.Priority).ThenBy(rule => rule.Id)
                .Select(rule => DiscountRule.Create(
                    rule.Id, rule.Version, rule.Name, Enum.Parse<DiscountType>(rule.Type), rule.Parameters.GetRawText(), rule.Conditions.GetRawText(),
                    rule.Priority, rule.Stackable, rule.OncePerClient, rule.ValidFrom, rule.ValidTo, rule.IsActive))
                .ToList());
        }
    }

    public Task<IReadOnlyList<DiscountRuleDto>> ListLatestAsync(bool? active, CancellationToken cancellationToken)
    {
        lock (_versions)
        {
            return Task.FromResult<IReadOnlyList<DiscountRuleDto>>(
                Latest().Where(rule => active is null || rule.IsActive == active).OrderBy(rule => rule.Priority).ThenBy(rule => rule.Id).ToList());
        }
    }

    public Task<DiscountRuleDto?> GetLatestAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_versions)
        {
            return Task.FromResult(Latest().FirstOrDefault(rule => rule.Id == id));
        }
    }

    public Task<bool> AddVersionAsync(DiscountRuleDto rule, CancellationToken cancellationToken)
    {
        lock (_versions)
        {
            if (_versions.Any(stored => stored.Id == rule.Id && stored.Version == rule.Version))
            {
                return Task.FromResult(false);
            }

            _versions.Add(rule);
            return Task.FromResult(true);
        }
    }

    public IReadOnlyList<DiscountRuleDto> Versions
    {
        get
        {
            lock (_versions)
            {
                return _versions.ToList();
            }
        }
    }

    private IEnumerable<DiscountRuleDto> Latest() => _versions.GroupBy(rule => rule.Id).Select(group => group.MaxBy(rule => rule.Version)!);
}

/// <summary>Stores applied discounts and enforces once-per-client like the partial unique index.</summary>
internal sealed class InMemoryOrderDiscountStore : IOrderDiscountStore
{
    private readonly ConcurrentDictionary<(Guid Rule, Guid Client), Guid> _oncePerClient = new();
    private readonly ConcurrentQueue<(Guid OrderId, AppliedDiscount Discount)> _stored = new();

    public IReadOnlyCollection<(Guid OrderId, AppliedDiscount Discount)> Stored => _stored.ToList();

    public Task AddAsync(Order order, IReadOnlyList<AppliedDiscount> discounts, CancellationToken cancellationToken)
    {
        foreach (var discount in discounts.Where(discount => discount.OncePerClient))
        {
            if (!_oncePerClient.TryAdd((discount.RuleId, order.Client.Id), order.Id))
            {
                throw new DiscountAlreadyUsedException("A once-per-client discount in this order was already used by the client.", new InvalidOperationException());
            }
        }

        foreach (var discount in discounts)
        {
            _stored.Enqueue((order.Id, discount));
        }

        return Task.CompletedTask;
    }
}

/// <summary>Remembers the events written to the event log, in order.</summary>
internal sealed class InMemoryOrderEventLog : IOrderEventLog
{
    private readonly ConcurrentQueue<IDomainEvent> _entries = new();

    public IReadOnlyCollection<IDomainEvent> Entries => _entries.ToList();

    public Task AppendAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        _entries.Enqueue(domainEvent);
        return Task.CompletedTask;
    }
}

/// <summary>Confirmation rule versions kept in a list, with the same "latest version wins" reading as the SQL repository.</summary>
internal sealed class InMemoryConfirmationRuleRepository : IConfirmationRuleRepository
{
    private readonly List<ConfirmationRuleDto> _versions = [];

    public IReadOnlyList<ConfirmationRuleDto> Versions
    {
        get
        {
            lock (_versions)
            {
                return _versions.ToList();
            }
        }
    }

    public Task<IReadOnlyList<ConfirmationRule>> GetActiveAsync(CancellationToken cancellationToken)
    {
        lock (_versions)
        {
            return Task.FromResult<IReadOnlyList<ConfirmationRule>>(Latest().Where(rule => rule.IsActive).OrderBy(rule => rule.Priority).ThenBy(rule => rule.Id)
                .Select(rule => ConfirmationRule.Create(
                    rule.Id, rule.Version, rule.Name, Enum.Parse<ConfirmationAction>(rule.Action), rule.Conditions.GetRawText(), rule.Priority, rule.IsActive))
                .ToList());
        }
    }

    public Task<IReadOnlyList<ConfirmationRuleDto>> ListLatestAsync(bool? active, CancellationToken cancellationToken)
    {
        lock (_versions)
        {
            return Task.FromResult<IReadOnlyList<ConfirmationRuleDto>>(
                Latest().Where(rule => active is null || rule.IsActive == active).OrderBy(rule => rule.Priority).ThenBy(rule => rule.Id).ToList());
        }
    }

    public Task<ConfirmationRuleDto?> GetLatestAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_versions)
        {
            return Task.FromResult(Latest().FirstOrDefault(rule => rule.Id == id));
        }
    }

    public Task<bool> AddVersionAsync(ConfirmationRuleDto rule, CancellationToken cancellationToken)
    {
        lock (_versions)
        {
            if (_versions.Any(stored => stored.Id == rule.Id && stored.Version == rule.Version))
            {
                return Task.FromResult(false);
            }

            _versions.Add(rule);
            return Task.FromResult(true);
        }
    }

    private IEnumerable<ConfirmationRuleDto> Latest() => _versions.GroupBy(rule => rule.Id).Select(group => group.MaxBy(rule => rule.Version)!);
}

/// <summary>Counts how often an admin write emptied the cache.</summary>
internal sealed class CountingActiveConfirmationRules(IConfirmationRuleRepository repository) : IActiveConfirmationRules
{
    private int _invalidations;

    public int Invalidations => Volatile.Read(ref _invalidations);

    public Task<IReadOnlyList<ConfirmationRule>> GetAsync(CancellationToken cancellationToken) => repository.GetActiveAsync(cancellationToken);

    public void Invalidate() => Interlocked.Increment(ref _invalidations);
}
