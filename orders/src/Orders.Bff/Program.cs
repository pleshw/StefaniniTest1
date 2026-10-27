using Orders.Bff;
using Orders.Bff.Clients;
using Orders.Bff.Routing;
using ServiceDefaults;

if (HealthCheckCommand.IsRequested(args))
{
    return await HealthCheckCommand.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection(BffOptions.SectionName).Get<BffOptions>() ?? new BffOptions();

builder.AddServiceDefaults();
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodySize);
builder.Services.AddBffClients(options);
builder.Services.AddBffProxy(options);

var app = builder.Build();

app.MapServiceDefaults();
app.UseBffRequestSizeLimit(options.MaxRequestBodySize);
app.MapBffProxy();

app.Run();

return 0;

public partial class Program;
