using Orders.Bff;
using Orders.Bff.Clients;
using Orders.Bff.Routing;
using Orders.Bff.Security;
using ServiceDefaults;

if (HealthCheckCommand.IsRequested(args))
{
    return await HealthCheckCommand.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection(BffOptions.SectionName).Get<BffOptions>() ?? new BffOptions();
var devLogin = builder.Configuration.GetSection(DevLoginOptions.SectionName).Get<DevLoginOptions>() ?? new DevLoginOptions();

builder.AddServiceDefaults();
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodySize);
builder.Services.Configure<ServiceTokenOptions>(builder.Configuration.GetSection(ServiceTokenOptions.SectionName));
builder.Services.Configure<DevLoginOptions>(builder.Configuration.GetSection(DevLoginOptions.SectionName));
builder.Services.AddBffAuthentication();
builder.Services.AddBffClients(options);
builder.Services.AddBffProxy(options);

var app = builder.Build();

app.MapServiceDefaults();
app.UseBffRequestSizeLimit(options.MaxRequestBodySize);
app.UseBffCsrfHeader();
app.UseAuthentication();
app.UseAuthorization();
app.MapBffSession(devLogin);
app.MapBffProxy();

app.Run();

return 0;

public partial class Program;
