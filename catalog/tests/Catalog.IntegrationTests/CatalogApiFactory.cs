using Catalog.Application.Products;
using Catalog.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Catalog.IntegrationTests;

/// <summary>Runs the real Catalog.Api pipeline with an in-memory repository instead of MongoDB.</summary>
public sealed class CatalogApiFactory : WebApplicationFactory<Program>
{
    internal InMemoryProductRepository Products { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var indexer = services.Single(service => service.ImplementationType == typeof(MongoIndexInitializer));
            services.Remove(indexer);
            services.RemoveAll<IProductRepository>();
            services.AddSingleton<IProductRepository>(Products);
        });
    }
}
