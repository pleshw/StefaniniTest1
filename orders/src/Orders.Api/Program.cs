using Orders.Api.Endpoints;
using Orders.Api.Security;
using Orders.Application;
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
builder.Services.AddOrdersAuthentication();
builder.Services.AddOrdersApplication();

var app = builder.Build();

if (OrdersMigrator.IsRequested(args))
{
    return await OrdersMigrator.RunAsync(app);
}

app.MapServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();
app.MapOrderEndpoints();

app.Run();

return 0;

public partial class Program;
