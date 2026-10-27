using System.Reflection;
using Catalog.Api.Endpoints;
using Catalog.Api.Security;
using Catalog.Application;
using Catalog.Domain;
using Catalog.Infrastructure.Persistence;
using Catalog.Infrastructure.Seeding;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceDefaults;

if (HealthCheckCommand.IsRequested(args))
{
    return await HealthCheckCommand.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton(new CurrencyPolicy(
    builder.Configuration.GetSection("Catalog:AllowedCurrencies").Get<string[]>() ?? []));
builder.Services.AddCatalogMongo(builder.Configuration);
builder.Services.AddCatalogApplication();
builder.Services.AddCatalogSeed(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAdminAuthorization(builder.Configuration);

// The build starts the app to write catalog/openapi/catalog.v1.json; no database is available then.
if (Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider")
{
    builder.Services.RemoveAll<IHostedService>();
}

var app = builder.Build();

app.MapServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();
app.MapProductEndpoints();
app.MapAdminProductEndpoints();

app.Run();

return 0;

public partial class Program;
