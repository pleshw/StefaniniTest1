using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Orders.IntegrationTests;

/// <summary>Runs the real Orders.Api pipeline without a database: persistence (and Wolverine's durable store) is switched off.</summary>
public sealed class OrdersApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Orders:Persistence:Enabled", "false");
    }
}
