using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orders.Application;
using Orders.Application.Discounts;
using Orders.Application.Ordering;
using Orders.Infrastructure.Catalog;
using Orders.Infrastructure.Persistence;
using ServiceDefaults;

namespace Orders.IntegrationTests;

/// <summary>Checks the production wiring (no database is contacted: the host is built, never started).</summary>
public sealed class OrdersCompositionTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Build_PersistenceEnabled_ResolvesTheCreateOrderHandlerWithItsRealDependencies()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Orders"] = "Host=unused;Database=unused;Username=unused",
        });
        builder.AddServiceDefaults();
        builder.Services.AddOrdersPersistence(builder.Configuration);
        builder.Services.AddCatalogClient(builder.Configuration);
        builder.Services.AddOrdersApplication();

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<CreateOrderHandler>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IOrderTransaction>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IOrderEventPublisher>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IOrderRepository>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<ListOrdersHandler>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<GetOrderHandler>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<GetOrderHistoryHandler>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<QuoteOrderHandler>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IActiveDiscountRules>().ShouldNotBeNull();
    }
}
