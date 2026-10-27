using Microsoft.Extensions.DependencyInjection;
using Orders.Bff.Clients;
using Orders.Bff.UnitTests.Support;

namespace Orders.Bff.UnitTests;

/// <summary>The typed clients generated from the committed Orders and Catalog OpenAPI documents.</summary>
public sealed class BffClientTests : IAsyncDisposable
{
    private readonly BffFactory _factory = new();

    [Fact]
    [Trait("Category", "Unit")]
    public void GeneratedClients_AreRegistered()
    {
        using var scope = _factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<Clients.Orders.IOrdersApiClient>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<Clients.Catalog.ICatalogApiClient>().ShouldNotBeNull();
    }

    [Theory]
    [InlineData(BffClients.OrdersClientName, BffFactory.OrdersAddress)]
    [InlineData(BffClients.CatalogClientName, BffFactory.CatalogAddress)]
    [Trait("Category", "Unit")]
    public void GeneratedClient_UsesTheConfiguredBaseAddress(string name, string address)
    {
        var client = _factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        client.BaseAddress.ShouldBe(new Uri(address));
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();
}
