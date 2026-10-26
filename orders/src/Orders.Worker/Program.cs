using Orders.Worker;
using ServiceDefaults;

if (HealthCheckCommand.IsRequested(args))
{
    return await HealthCheckCommand.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOrdersWorker(builder.Configuration);

var app = builder.Build();

app.MapServiceDefaults();

app.Run();

return 0;

public partial class Program;
