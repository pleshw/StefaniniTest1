using Catalog.Api.Endpoints;
using Catalog.Api.Security;
using Catalog.Application.Products;
using Catalog.Domain;
using Catalog.Infrastructure.Persistence;
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
builder.Services.AddScoped<ListProductsHandler>();
builder.Services.AddScoped<GetProductHandler>();
builder.Services.AddScoped<QueryPricesHandler>();
builder.Services.AddScoped<UpsertProductHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAdminAuthorization();

var app = builder.Build();

app.MapServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();
app.MapProductEndpoints();
app.MapAdminProductEndpoints();

app.Run();

return 0;

public partial class Program;
