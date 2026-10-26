using Catalog.Application.Products;
using Catalog.Domain;
using MongoDB.Driver;

namespace Catalog.Infrastructure.Persistence;

public sealed class ProductRepository : IProductRepository
{
    private readonly IMongoClient _client;
    private readonly IMongoCollection<ProductDocument> _products;
    private readonly IMongoCollection<PriceHistoryDocument> _priceHistory;

    public ProductRepository(IMongoClient client, IMongoDatabase database)
    {
        CatalogClassMaps.Register();

        _client = client;
        _products = database.GetCollection<ProductDocument>(CatalogCollections.Products);
        _priceHistory = database.GetCollection<PriceHistoryDocument>(CatalogCollections.PriceHistory);
    }

    public async Task<Product?> GetBySkuAsync(Sku sku, CancellationToken cancellationToken)
    {
        var filter = Builders<ProductDocument>.Filter.Eq(document => document.Sku, sku.Value);

        var document = await _products.Find(filter).FirstOrDefaultAsync(cancellationToken);

        return document is null ? null : ToDomain(document);
    }

    public async Task<IReadOnlyList<Product>> SearchAsync(ProductSearch search, CancellationToken cancellationToken)
    {
        var filters = Builders<ProductDocument>.Filter;
        var conditions = new List<FilterDefinition<ProductDocument>> { filters.Eq(document => document.IsActive, true) };

        if (!string.IsNullOrWhiteSpace(search.Text))
        {
            conditions.Add(filters.Text(search.Text));
        }

        if (!string.IsNullOrWhiteSpace(search.Category))
        {
            conditions.Add(filters.Eq(document => document.Category, search.Category));
        }

        if (search.After is { } after)
        {
            conditions.Add(filters.Or(
                filters.Gt(document => document.Name, after.Name),
                filters.And(
                    filters.Eq(document => document.Name, after.Name),
                    filters.Gt(document => document.Sku, after.Sku))));
        }

        var documents = await _products.Find(filters.And(conditions))
            .SortBy(document => document.Name).ThenBy(document => document.Sku)
            .Limit(search.Limit)
            .ToListAsync(cancellationToken);

        return documents.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<ProductSummary>> GetManyAsync(
        IReadOnlyCollection<Sku> skus, CancellationToken cancellationToken)
    {
        var filter = Builders<ProductDocument>.Filter.In(document => document.Sku, skus.Select(sku => sku.Value));
        var projection = Builders<ProductDocument>.Projection
            .Include(document => document.Sku)
            .Include(document => document.Name)
            .Include(document => document.Category)
            .Include(document => document.Prices)
            .Include(document => document.IsActive)
            .Include(document => document.Version)
            .Exclude(document => document.Id);

        var documents = await _products.Find(filter).Project<ProductDocument>(projection).ToListAsync(cancellationToken);

        return documents
            .Select(document => new ProductSummary(
                Sku.Create(document.Sku),
                document.Name,
                document.Category,
                document.IsActive,
                document.Version,
                document.Prices.Select(ToMoney).ToList()))
            .ToList();
    }

    public async Task SaveAsync(
        Product product,
        int? expectedVersion,
        AuditStamp audit,
        IReadOnlyCollection<PriceHistoryEntry> priceHistory,
        CancellationToken cancellationToken)
    {
        using var session = await _client.StartSessionAsync(cancellationToken: cancellationToken);

        try
        {
            await session.WithTransactionAsync(
                async (transactionSession, token) =>
                {
                    if (expectedVersion is null)
                    {
                        await _products.InsertOneAsync(transactionSession, ToDocument(product, audit), cancellationToken: token);
                    }
                    else
                    {
                        await ReplaceIfVersionMatchesAsync(transactionSession, product, expectedVersion.Value, audit, token);
                    }

                    if (priceHistory.Count > 0)
                    {
                        await _priceHistory.InsertManyAsync(
                            transactionSession, priceHistory.Select(ToDocument), cancellationToken: token);
                    }

                    return true;
                },
                cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new ConcurrencyException($"Product {product.Sku} already exists.");
        }
    }

    private async Task ReplaceIfVersionMatchesAsync(
        IClientSessionHandle session, Product product, int expectedVersion, AuditStamp audit, CancellationToken token)
    {
        var filter = Builders<ProductDocument>.Filter.And(
            Builders<ProductDocument>.Filter.Eq(document => document.Sku, product.Sku.Value),
            Builders<ProductDocument>.Filter.Eq(document => document.Version, expectedVersion));
        var changes = ToDocument(product, audit);
        var update = Builders<ProductDocument>.Update
            .Set(document => document.Name, changes.Name)
            .Set(document => document.Description, changes.Description)
            .Set(document => document.Category, changes.Category)
            .Set(document => document.Attributes, changes.Attributes)
            .Set(document => document.Prices, changes.Prices)
            .Set(document => document.IsActive, changes.IsActive)
            .Set(document => document.Version, changes.Version)
            .Set(document => document.UpdatedAt, changes.UpdatedAt)
            .Set(document => document.UpdatedBy, changes.UpdatedBy);

        var result = await _products.UpdateOneAsync(session, filter, update, cancellationToken: token);

        if (result.MatchedCount == 0)
        {
            throw new ConcurrencyException($"Product {product.Sku} was changed by someone else (expected version {expectedVersion}).");
        }
    }

    private static ProductDocument ToDocument(Product product, AuditStamp audit) => new()
    {
        Sku = product.Sku.Value,
        Name = product.Name,
        Description = product.Description,
        Category = product.Category,
        Attributes = product.Attributes.ToDictionary(pair => pair.Key, pair => pair.Value),
        Prices = product.Prices.Select(price => new PriceDocument { Currency = price.Currency.Code, Amount = price.Amount }).ToList(),
        IsActive = product.IsActive,
        Version = product.Version,
        UpdatedAt = audit.UpdatedAt.UtcDateTime,
        UpdatedBy = audit.UpdatedBy,
    };

    private static PriceHistoryDocument ToDocument(PriceHistoryEntry entry) => new()
    {
        Sku = entry.Sku.Value,
        Currency = entry.Currency.Code,
        OldPrice = entry.OldPrice,
        NewPrice = entry.NewPrice,
        Version = entry.Version,
        ChangedBy = entry.ChangedBy,
        ChangedAt = entry.ChangedAt.UtcDateTime,
        TraceId = entry.TraceId,
    };

    private static Product ToDomain(ProductDocument document) => Product.Restore(
        Sku.Create(document.Sku),
        document.Name,
        document.Description,
        document.Category,
        document.Attributes,
        document.Prices.Select(ToMoney),
        document.IsActive,
        document.Version);

    private static Money ToMoney(PriceDocument price) => Money.Create(price.Amount, Currency.Create(price.Currency));
}
