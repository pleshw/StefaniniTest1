using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orders.Application.Catalog;
using Orders.Application.Confirmation;
using Orders.Application.DeadLetters;
using Orders.Application.Discounts;
using Orders.Application.Events;
using Orders.Application.Idempotency;
using Orders.Application.Ordering;
using Orders.Infrastructure.Persistence;

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

    internal InMemoryActiveDiscountRules DiscountRules { get; } = new();

    internal InMemoryOrderDiscountStore Discounts { get; } = new();

    internal InMemoryOrderEventLog EventLog { get; } = new();

    internal InMemoryConfirmationRuleRepository ConfirmationRuleStore { get; } = new();

    internal InMemoryDeadLetters DeadLetters { get; } = new();

    internal CountingActiveConfirmationRules ConfirmationRules { get; }

    internal InMemoryDiscountRuleRepository DiscountRuleStore { get; } = new();

    /// <summary>
    /// Reads the active rules from <see cref="DiscountRuleStore"/> through the real one-hour cache, so only an
    /// invalidation makes an admin write visible. Set before the first request.
    /// </summary>
    internal bool CachedDiscountRules { get; set; }

    public OrdersApiFactory()
    {
        ConfirmationRules = new CountingActiveConfirmationRules(ConfirmationRuleStore);
    }

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
            services.AddSingleton<IOrderEventLog>(EventLog);
            services.AddSingleton<IOrderEventLogQueries>(EventLog);
            services.AddSingleton<IConfirmationRuleRepository>(ConfirmationRuleStore);
            services.AddSingleton<IDeadLetters>(DeadLetters);
            services.AddSingleton<IActiveConfirmationRules>(ConfirmationRules);
            services.AddSingleton<IDiscountRuleRepository>(DiscountRuleStore);

            if (CachedDiscountRules)
            {
                services.AddSingleton<IActiveDiscountRules>(new CachedActiveDiscountRules(
                    DiscountRuleStore, new MemoryCache(new MemoryCacheOptions()), new DiscountOptions { CacheDuration = TimeSpan.FromHours(1) }));
            }
            else
            {
                services.AddSingleton<IActiveDiscountRules>(DiscountRules);
            }

            services.AddSingleton<IOrderDiscountStore>(Discounts);
            services.AddSingleton<IOrderTransaction>(new SerializingOrderTransaction());
            services.AddSingleton(TimeProvider.System);

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }
}
