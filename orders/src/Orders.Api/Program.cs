using System.Reflection;
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

// The build writes the OpenAPI document by starting this host through GetDocument.Insider: it needs the endpoints only,
// so it gets no database, outbox or broker.
if (Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider")
{
    builder.Configuration["Orders:Persistence:Enabled"] = "false";
}

builder.Services.AddOrdersPersistence(builder.Configuration);
builder.Services.AddCatalogClient(builder.Configuration);
builder.Services.AddOrdersAuthentication(builder.Configuration);
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
app.MapAdminOrderEndpoints();
app.MapAdminDiscountRuleEndpoints();
app.MapAdminConfirmationRuleEndpoints();
app.MapAdminDeadLetterEndpoints();
app.MapAdminEventEndpoints();

app.Run();

return 0;

public partial class Program;
