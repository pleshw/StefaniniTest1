using Catalog.Application.Products;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Catalog.Infrastructure.Seeding;

/// <summary>Inserts the development catalog at startup. Products already stored are left untouched.</summary>
public sealed class CatalogSeeder(IProductRepository repository, TimeProvider clock) : IHostedService
{
    public const string SettingName = "Catalog:Seed:Enabled";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var audit = new AuditStamp("system:seed", clock.GetUtcNow());

        foreach (var product in SeedProducts.Create())
        {
            if (await repository.GetBySkuAsync(product.Sku, cancellationToken) is not null)
            {
                continue;
            }

            try
            {
                await repository.SaveAsync(product, null, audit, [], cancellationToken);
            }
            catch (ConcurrencyException)
            {
                // Another instance inserted the same SKU first; the seed is idempotent.
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class CatalogSeedExtensions
{
    /// <summary>
    /// Registers the seeder only when <c>Catalog:Seed:Enabled</c> is true: Development settings and the compose file
    /// turn it on, staging and production never do. Call it after <c>AddCatalogMongo</c> so the indexes exist first.
    /// </summary>
    public static IServiceCollection AddCatalogSeed(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration.GetValue<bool>(CatalogSeeder.SettingName))
        {
            services.AddHostedService<CatalogSeeder>();
        }

        return services;
    }
}
