using Microsoft.EntityFrameworkCore;
using Orders.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Orders.IntegrationTests;

/// <summary>A PostgreSQL 17 container, as in compose, with the EF migrations applied.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    internal OrdersDbContext NewContext() =>
        new(new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(ConnectionString).Options);
}
