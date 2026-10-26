using System.Collections.Concurrent;
using MongoDB.Bson;
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

    public ConcurrentQueue<CapturedCommand> Commands { get; } = new();

    public IMongoClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var settings = MongoClientSettings.FromConnectionString(_container.GetConnectionString());
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(started => Commands.Enqueue(CapturedCommand.From(started)));
        Client = new MongoClient(settings);
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        await _container.DisposeAsync();
    }

    public IMongoDatabase NewDatabase() => Client.GetDatabase($"catalog_{Guid.NewGuid():N}");
}

/// <summary>A command-started event copied at publish time: the driver disposes the raw command afterwards.</summary>
public sealed record CapturedCommand(string CommandName, string DatabaseName, BsonDocument Command)
{
    internal static CapturedCommand From(CommandStartedEvent started) => new(
        started.CommandName,
        started.DatabaseNamespace.DatabaseName,
        BsonDocument.Parse(started.Command.ToJson()));
}
