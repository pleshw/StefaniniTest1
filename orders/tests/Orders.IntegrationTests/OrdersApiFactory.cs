using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orders.Application.Catalog;
using Orders.Application.Idempotency;
using Orders.Application.Ordering;

namespace Orders.IntegrationTests;

/// <summary>
/// Runs the real Orders.Api pipeline without PostgreSQL, Wolverine or Catalog: persistence is switched off, the stores
/// are in memory, the catalog is a fake and the header-driven test authentication scheme replaces the identity provider.
/// </summary>
public sealed class OrdersApiFactory : WebApplicationFactory<Program>
{
    internal InMemoryOrderRepository Orders { get; } = new();

    internal InMemoryIdempotencyStore Idempotency { get; } = new();

    internal RecordingOrderEventPublisher Events { get; } = new();

    internal FakeCatalogPriceProvider Catalog { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Orders:Persistence:Enabled", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICatalogPriceProvider>();
            services.AddSingleton<ICatalogPriceProvider>(Catalog);
            services.AddSingleton<IOrderRepository>(Orders);
            services.AddSingleton<IOrderQueries>(new InMemoryOrderQueries(Orders));
            services.AddSingleton<IIdempotencyStore>(Idempotency);
            services.AddSingleton<IOrderEventPublisher>(Events);
            services.AddSingleton<IOrderTransaction>(new SerializingOrderTransaction());
            services.AddSingleton(TimeProvider.System);

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }
}
