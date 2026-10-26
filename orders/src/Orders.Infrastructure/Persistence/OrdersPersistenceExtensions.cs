using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orders.Application.Idempotency;
using Orders.Application.Ordering;
using Wolverine;
using Wolverine.Postgresql;

namespace Orders.Infrastructure.Persistence;

public sealed record OrdersPersistenceOptions
{
    public const string SectionName = "Orders:Persistence";

    public const string ConnectionStringName = "Orders";

    public const string WolverineSchema = "wolverine";

    public const string DefaultConnectionString = "Host=localhost;Port=5432;Database=orders;Username=orders";

    /// <summary>Turned off only by tests that start a host without a database.</summary>
    public bool Enabled { get; init; } = true;
}

public static class OrdersPersistenceExtensions
{
    /// <summary>
    /// Registers the Orders DbContext, the order repository and Wolverine's PostgreSQL message persistence (outbox and
    /// inbox tables live in the <c>wolverine</c> schema and are created by <see cref="OrdersMigrator"/>, not by the host).
    /// </summary>
    public static IServiceCollection AddOrdersPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(OrdersPersistenceOptions.SectionName).Get<OrdersPersistenceOptions>() ?? new OrdersPersistenceOptions();

        if (!options.Enabled)
        {
            return services;
        }

        var connectionString = configuration.GetConnectionString(OrdersPersistenceOptions.ConnectionStringName)
            ?? OrdersPersistenceOptions.DefaultConnectionString;

        services.AddDbContext<OrdersDbContext>(builder => builder.UseNpgsql(connectionString));
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddWolverine(wolverine =>
        {
            wolverine.PersistMessagesWithPostgresql(connectionString, OrdersPersistenceOptions.WolverineSchema);
            wolverine.AutoBuildMessageStorageOnStartup = JasperFx.AutoCreate.None;
        });

        return services;
    }
}

/// <summary>
/// The <c>migrate</c> argument of Orders.Api, run by the <c>orders-migrator</c> container: applies the EF Core
/// migrations, then creates Wolverine's tables (<c>wolverine resources setup</c>), then exits.
/// </summary>
public static class OrdersMigrator
{
    private const string _argument = "migrate";

    public static bool IsRequested(string[] args) => args.Length == 1 && args[0] == _argument;

    public static async Task<int> RunAsync(IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

        await context.Database.MigrateAsync();
        await host.SetupResources();

        return 0;
    }
}
