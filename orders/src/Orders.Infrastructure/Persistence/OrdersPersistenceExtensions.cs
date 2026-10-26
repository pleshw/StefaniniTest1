using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orders.Application.Confirmation;
using Orders.Application.Discounts;
using Orders.Application.Idempotency;
using Orders.Application.Ordering;
using Orders.Domain;
using Orders.Infrastructure.Messaging;
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

    /// <summary>
    /// Lets the host create Wolverine's tables itself when it starts. Off by default: the migrator owns the schema.
    /// Turned on only by tests that start the API on a fresh database.
    /// </summary>
    public bool AutoCreateMessageStorage { get; init; }
}

public static class OrdersPersistenceExtensions
{
    /// <summary>
    /// Registers the Orders DbContext, the order repository and Wolverine's PostgreSQL message persistence (outbox and
    /// inbox tables live in the <c>wolverine</c> schema and are created by <see cref="OrdersMigrator"/>, not by the host).
    /// <paramref name="configureMessaging"/> lets a host add its own listeners and error policies.
    /// </summary>
    public static IServiceCollection AddOrdersPersistence(
        this IServiceCollection services, IConfiguration configuration, Action<WolverineOptions>? configureMessaging = null)
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
        services.AddScoped<IOrderQueries, OrderQueries>();
        services.AddScoped<IDiscountRuleRepository, DiscountRuleRepository>();
        services.AddMemoryCache();
        services.AddSingleton(configuration.GetSection(DiscountOptions.SectionName).Get<DiscountOptions>() ?? new DiscountOptions());
        services.AddScoped<IActiveDiscountRules, CachedActiveDiscountRules>();
        services.AddScoped<IOrderDiscountStore, OrderDiscountStore>();
        services.AddSingleton(configuration.GetSection(ConfirmationOptions.SectionName).Get<ConfirmationOptions>() ?? new ConfirmationOptions());
        services.AddScoped<IConfirmationRuleRepository, ConfirmationRuleRepository>();
        services.AddScoped<IActiveConfirmationRules, CachedActiveConfirmationRules>();
        services.AddScoped<IOrderEventLog, OrderEventLog>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IOrderTransaction, OrdersTransaction>();
        services.AddScoped<IOrderEventPublisher, WolverineOrderEventPublisher>();
        services.AddWolverine(wolverine =>
        {
            wolverine.PersistMessagesWithPostgresql(connectionString, OrdersPersistenceOptions.WolverineSchema);
            wolverine.AutoBuildMessageStorageOnStartup = options.AutoCreateMessageStorage ? JasperFx.AutoCreate.CreateOrUpdate : JasperFx.AutoCreate.None;
            wolverine.UseOrdersRabbitMq(configuration);
            configureMessaging?.Invoke(wolverine);
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

        if (host.Services.GetRequiredService<IConfiguration>().GetSection(ConfirmationOptions.SectionName).Get<ConfirmationOptions>()?.SeedEnabled == true)
        {
            await ConfirmationRuleSeeder.SeedAsync(context, scope.ServiceProvider.GetRequiredService<TimeProvider>(), CancellationToken.None);
        }

        await host.SetupResources();

        return 0;
    }
}
