using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orders.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build the context without starting any host. The connection is never opened to create
/// a migration or a SQL script, so the default needs no database.
/// </summary>
internal sealed class OrdersDesignTimeFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Orders") ?? OrdersPersistenceOptions.DefaultConnectionString;

        return new OrdersDbContext(new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(connectionString).Options);
    }
}
