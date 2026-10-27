using Catalog.Application.Products;
using Catalog.Infrastructure.Persistence;
using Catalog.Infrastructure.Seeding;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Catalog.IntegrationTests;

/// <summary>
/// Runs the real Catalog.Api pipeline with an in-memory repository instead of MongoDB and the header-driven test
/// authentication scheme instead of the real identity provider.
/// </summary>
public sealed class CatalogApiFactory : WebApplicationFactory<Program>
{
    internal InMemoryProductRepository Products { get; } = new();

    /// <summary>The key the API validates service tokens with (the BFF signs with the same key).</summary>
    public const string ServiceTokenSigningKey = "catalog-tests-service-token-signing-key-0123456789";

    /// <summary>
    /// The header-driven Test scheme replaces the real service-token validation; set to false (before the first request)
    /// to exercise the real JWT bearer scheme.
    /// </summary>
    internal bool UseTestAuthentication { get; init; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ServiceTokens:SigningKey", ServiceTokenSigningKey);
        builder.ConfigureServices(services =>
        {
            var indexer = services.Single(service => service.ImplementationType == typeof(MongoIndexInitializer));
            services.Remove(indexer);
            foreach (var seeder in services.Where(service => service.ImplementationType == typeof(CatalogSeeder)).ToList())
            {
                services.Remove(seeder);
            }

            services.RemoveAll<IProductRepository>();
            services.AddSingleton<IProductRepository>(Products);

            if (UseTestAuthentication)
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            }
        });
    }
}
