using Orders.Infrastructure.Catalog;
using Orders.Infrastructure.Persistence;
using ServiceDefaults;

if (HealthCheckCommand.IsRequested(args))
{
    return await HealthCheckCommand.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOrdersPersistence(builder.Configuration);
builder.Services.AddCatalogClient(builder.Configuration);

var app = builder.Build();

if (OrdersMigrator.IsRequested(args))
{
    return await OrdersMigrator.RunAsync(app);
}

app.MapServiceDefaults();

app.Run();

return 0;

public partial class Program;
