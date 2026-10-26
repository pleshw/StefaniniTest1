using Catalog.Application.Products;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Catalog.Infrastructure.Persistence;

public sealed record MongoOptions
{
    public const string SectionName = "Mongo";

    public string ConnectionString { get; init; } = "mongodb://localhost:27017/?replicaSet=rs0&directConnection=true";

    public string Database { get; init; } = "catalog";
}

public static class CatalogMongoExtensions
{
    public static IServiceCollection AddCatalogMongo(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(MongoOptions.SectionName).Get<MongoOptions>() ?? new MongoOptions();

        CatalogClassMaps.Register();

        services.AddSingleton<IMongoClient>(_ => new MongoClient(options.ConnectionString));
        services.AddSingleton(provider => provider.GetRequiredService<IMongoClient>().GetDatabase(options.Database));
        services.AddSingleton<IProductRepository, ProductRepository>();
        services.AddHostedService<MongoIndexInitializer>();

        return services;
    }
}
