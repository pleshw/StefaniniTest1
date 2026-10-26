using Catalog.Domain;
using ServiceDefaults;

if (HealthCheckCommand.IsRequested(args))
{
    return await HealthCheckCommand.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton(new CurrencyPolicy(
    builder.Configuration.GetSection("Catalog:AllowedCurrencies").Get<string[]>() ?? []));

var app = builder.Build();

app.MapServiceDefaults();

app.Run();

return 0;

public partial class Program;
