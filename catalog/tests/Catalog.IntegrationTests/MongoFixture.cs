using System.Collections.Concurrent;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Testcontainers.MongoDb;

namespace Catalog.IntegrationTests;

/// <summary>A single-node MongoDB 8 replica set, as in compose, so transactions work.</summary>
public sealed class MongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:8")
        .WithReplicaSet()
        .Build();

    public ConcurrentQueue<CommandStartedEvent> Commands { get; } = new();

    public IMongoClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var settings = MongoClientSettings.FromConnectionString(_container.GetConnectionString());
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(Commands.Enqueue);
        Client = new MongoClient(settings);
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        await _container.DisposeAsync();
    }

    public IMongoDatabase NewDatabase() => Client.GetDatabase($"catalog_{Guid.NewGuid():N}");
}
