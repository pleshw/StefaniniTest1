using Catalog.Application.Products;
using Catalog.Domain;
using Catalog.Infrastructure.Seeding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Catalog.IntegrationTests;

/// <summary>Seed checks that need no database, so they run as unit tests.</summary>
public sealed class CatalogSeedTests
{
    private static readonly string[] _currencies = ["BRL", "USD"];

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_Always_ReturnsFiftyDistinctProductsPricedInBrlAndUsd()
    {
        var products = SeedProducts.Create();

        products.Count.ShouldBe(50);
        products.Select(product => product.Sku.Value).Distinct().Count().ShouldBe(50);
        products.ShouldAllBe(product => product.IsActive && product.Version == 1);
        products.ShouldAllBe(product => product.Prices.Select(price => price.Currency.Code).Order().SequenceEqual(_currencies));
        products.ShouldAllBe(product => product.Prices.All(price => price.Amount > 0));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartAsync_SomeProductsStored_SavesOnlyTheMissingOnesAsNewDocuments()
    {
        var repository = Substitute.For<IProductRepository>();
        var stored = SeedProducts.Create()[0];
        repository.GetBySkuAsync(Arg.Any<Sku>(), Token).Returns(call => call.Arg<Sku>() == stored.Sku ? stored : null);
        var seeder = new CatalogSeeder(repository, TimeProvider.System);

        await seeder.StartAsync(Token);

        await repository.Received(49).SaveAsync(
            Arg.Is<Product>(product => product.Sku != stored.Sku),
            null,
            Arg.Is<AuditStamp>(audit => audit.UpdatedBy == "system:seed"),
            Arg.Is<IReadOnlyCollection<PriceHistoryEntry>>(history => history.Count == 0),
            Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartAsync_AnotherInstanceSeededFirst_IgnoresTheConflict()
    {
        var repository = Substitute.For<IProductRepository>();
        repository.SaveAsync(default!, default, default!, default!, Token)
            .ReturnsForAnyArgs(Task.FromException(new ConcurrencyException("exists")));
        var seeder = new CatalogSeeder(repository, TimeProvider.System);

        await seeder.StartAsync(Token);

        await repository.ReceivedWithAnyArgs(50).SaveAsync(default!, default, default!, default!, Token);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void AddCatalogSeed_Setting_RegistersTheSeederOnlyWhenEnabled(string? enabled, bool registered)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(enabled is null ? [] : new Dictionary<string, string?> { ["Catalog:Seed:Enabled"] = enabled })
            .Build();
        var services = new ServiceCollection();

        services.AddCatalogSeed(configuration);

        services.Any(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(CatalogSeeder))
            .ShouldBe(registered);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
