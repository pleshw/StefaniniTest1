using Catalog.Api.Endpoints;
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

var app = builder.Build();

app.MapServiceDefaults();
app.MapProductEndpoints();

app.Run();

return 0;

public partial class Program;
