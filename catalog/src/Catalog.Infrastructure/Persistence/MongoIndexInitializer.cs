using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Catalog.Infrastructure.Persistence;

/// <summary>Fails fast when MongoDB is not a replica set (price changes need transactions), then creates the indexes.</summary>
public sealed class MongoIndexInitializer(IMongoDatabase database) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        CatalogClassMaps.Register();

        await EnsureReplicaSetAsync(cancellationToken);
        await CreateProductIndexesAsync(cancellationToken);
        await CreatePriceHistoryIndexesAsync(cancellationToken);
        await CreateIdempotencyIndexesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureReplicaSetAsync(CancellationToken cancellationToken)
    {
        var hello = await database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: cancellationToken);

        if (!hello.Contains("setName"))
        {
            throw new InvalidOperationException(
                "MongoDB must run as a replica set: the catalog writes price history in transactions.");
        }
    }

    private async Task CreateProductIndexesAsync(CancellationToken cancellationToken)
    {
        var products = database.GetCollection<ProductDocument>(CatalogCollections.Products);
        var keys = Builders<ProductDocument>.IndexKeys;

        await products.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<ProductDocument>(
                    keys.Ascending(document => document.Sku),
                    new CreateIndexOptions { Name = "ux_products_sku", Unique = true }),
                new CreateIndexModel<ProductDocument>(
                    keys.Ascending(document => document.Category).Ascending(document => document.Name),
                    new CreateIndexOptions { Name = "ix_products_category_name" }),
                new CreateIndexModel<ProductDocument>(
                    keys.Text(document => document.Name).Text(document => document.Description),
                    new CreateIndexOptions { Name = "tx_products_name_description" }),
            ],
            cancellationToken);
    }

    private async Task CreatePriceHistoryIndexesAsync(CancellationToken cancellationToken)
    {
        var history = database.GetCollection<PriceHistoryDocument>(CatalogCollections.PriceHistory);
        var keys = Builders<PriceHistoryDocument>.IndexKeys;

        await history.Indexes.CreateOneAsync(
            new CreateIndexModel<PriceHistoryDocument>(
                keys.Ascending(document => document.Sku).Descending(document => document.ChangedAt),
                new CreateIndexOptions { Name = "ix_price_history_sku_changed_at" }),
            cancellationToken: cancellationToken);
    }

    private async Task CreateIdempotencyIndexesAsync(CancellationToken cancellationToken)
    {
        var requests = database.GetCollection<IdempotencyDocument>(CatalogCollections.IdempotencyRequests);
        var keys = Builders<IdempotencyDocument>.IndexKeys;

        await requests.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<IdempotencyDocument>(
                    keys.Ascending(document => document.Scope).Ascending(document => document.Key),
                    new CreateIndexOptions { Name = "ux_idempotency_scope_key", Unique = true }),
                new CreateIndexModel<IdempotencyDocument>(
                    keys.Ascending(document => document.ExpiresAt),
                    new CreateIndexOptions { Name = "ttl_idempotency_expires_at", ExpireAfter = TimeSpan.Zero }),
            ],
            cancellationToken);
    }
}
