using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Orders.Domain;
using Orders.Infrastructure.Persistence;

namespace Orders.IntegrationTests;

/// <summary>Model and mapping checks that need no database, so they run as unit tests.</summary>
public sealed class OrdersModelTests
{
    private static readonly DateTimeOffset _now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Unit")]
    public void Model_MatchesTheMigrationSnapshot_SoNoMigrationIsMissing()
    {
        using var context = NewContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var snapshotModel = migrations.ModelSnapshot!.Model;
        var designTimeModel = context.GetService<IDesignTimeModel>().Model;

        var differences = differ.GetDifferences(
            context.GetService<IModelRuntimeInitializer>().Initialize(snapshotModel).GetRelationalModel(),
            designTimeModel.GetRelationalModel());

        differences.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Model_Tables_MatchThePlanDdl()
    {
        using var context = NewContext();

        var tables = context.Model.GetEntityTypes().Select(type => type.GetTableName()).Order().ToList();

        tables.ShouldBe(["idempotency_requests", "order_items", "order_status_history", "orders"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Model_Orders_UsesRowVersionAsTheConcurrencyTokenAndMoneyPrecision()
    {
        using var context = NewContext();
        var orders = context.Model.FindEntityType(typeof(OrderRecord))!;

        orders.FindProperty(nameof(OrderRecord.RowVersion))!.IsConcurrencyToken.ShouldBeTrue();
        orders.FindProperty(nameof(OrderRecord.RowVersion))!.GetColumnName().ShouldBe("row_version");
        orders.FindProperty(nameof(OrderRecord.Total))!.GetColumnType().ShouldBe("numeric(19,4)");
        orders.FindProperty(nameof(OrderRecord.Currency))!.GetColumnType().ShouldBe("char(3)");
        orders.FindProperty(nameof(OrderRecord.SearchVector))!.GetColumnName().ShouldBe("search_vector");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomain_AfterToRecord_RebuildsTheSameOrder()
    {
        var brl = Currency.Create("BRL");
        var order = Order.Place(
            Guid.NewGuid(),
            "ORD-MAP-1",
            ClientRef.Create(Guid.NewGuid(), "Ana"),
            "shopper-1",
            OrderChannel.Shop,
            [OrderItem.Create("SKU-1", "Keyboard", Money.Create(19.99m, brl), 3, 3, Money.Create(5m, brl))],
            _now);
        order.Confirm("system:worker", _now.AddMinutes(1));

        var record = order.ToRecord();
        var restored = record.ToDomain();

        record.Channel.ShouldBe(OrderChannel.Shop);
        record.History.Select(change => change.Seq).ShouldBe([1, 2]);
        record.Items.Single().LineNo.ShouldBe(1);
        restored.Items.ShouldBe(order.Items);
        restored.StatusHistory.ShouldBe(order.StatusHistory);
        restored.Total.ShouldBe(order.Total);
        restored.Status.ShouldBe(OrderStatus.Confirmed);
        restored.RowVersion.ShouldBe(1);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "migrate" }, true)]
    [InlineData(new[] { "healthcheck" }, false)]
    [InlineData(new[] { "migrate", "extra" }, false)]
    public void IsRequested_Arguments_OnlyTheMigrateArgumentCounts(string[] args, bool expected)
    {
        OrdersMigrator.IsRequested(args).ShouldBe(expected);
    }

    private static OrdersDbContext NewContext() =>
        new(new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql("Host=unused;Database=unused").Options);
}
