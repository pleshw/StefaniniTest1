using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orders.Application.Discounts;
using Orders.Application.Ordering;
using Orders.Domain;
using Orders.Domain.Discounts;
using Orders.Infrastructure.Persistence;

namespace Orders.IntegrationTests;

/// <summary>Discount rule storage and redemption constraints on PostgreSQL 17 (Testcontainers) with the real migrations.</summary>
public sealed class DiscountRuleRepositoryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset _now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetActiveAsync_NewVersion_SupersedesTheOldOne()
    {
        var id = Guid.NewGuid();
        await Insert(Rule(id, 1, percent: 5));
        await Insert(Rule(id, 2, percent: 15));

        var rule = (await Active()).Single(found => found.Id == id);

        rule.Version.ShouldBe(2);
        rule.Strategy.ShouldBeOfType<PercentageDiscount>().Parameters.Percent.ShouldBe(15m);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetActiveAsync_InactiveRules_AreIgnored()
    {
        var inactive = Guid.NewGuid();
        var deactivated = Guid.NewGuid();
        await Insert(Rule(inactive, 1, percent: 5, isActive: false));
        await Insert(Rule(deactivated, 1, percent: 5));
        await Insert(Rule(deactivated, 2, percent: 5, isActive: false));

        var ids = (await Active()).Select(rule => rule.Id).ToList();

        ids.ShouldNotContain(inactive);
        ids.ShouldNotContain(deactivated);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Redemptions_SecondOncePerClientRedemption_IsBlockedByThePartialUniqueIndex()
    {
        var rule = Guid.NewGuid();
        var client = Guid.NewGuid();
        await Insert(Rule(rule, 1, percent: 5, oncePerClient: true));
        var first = await InsertOrder(client);
        var second = await InsertOrder(client);
        await Redeem(rule, client, first, oncePerClient: true);

        var conflict = await Should.ThrowAsync<DbUpdateException>(() => Redeem(rule, client, second, oncePerClient: true));

        var postgresError = conflict.InnerException.ShouldBeOfType<PostgresException>();
        postgresError.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        postgresError.ConstraintName.ShouldBe(DiscountRedemptionRecord.OncePerClientIndex);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Redemptions_RuleNotOncePerClient_CanBeRedeemedOnEveryOrder()
    {
        var rule = Guid.NewGuid();
        var client = Guid.NewGuid();
        await Insert(Rule(rule, 1, percent: 5));

        await Redeem(rule, client, await InsertOrder(client), oncePerClient: false);
        await Redeem(rule, client, await InsertOrder(client), oncePerClient: false);

        await using var context = postgres.NewContext();
        (await context.DiscountRedemptions.CountAsync(redemption => redemption.DiscountRuleId == rule, Token)).ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OrderDiscounts_ReferenceTheRuleVersion()
    {
        var rule = Guid.NewGuid();
        await Insert(Rule(rule, 1, percent: 5));
        var order = await InsertOrder(Guid.NewGuid());
        await using var context = postgres.NewContext();

        context.OrderDiscounts.Add(new OrderDiscountRecord { OrderId = order, DiscountRuleId = rule, RuleVersion = 9, Amount = 1m });

        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Token));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetLatestAsync_ManyVersions_ReturnsTheHighestAndItsJson()
    {
        var id = Guid.NewGuid();
        await Insert(Rule(id, 1, percent: 5));
        await Insert(Rule(id, 2, percent: 15, isActive: false));

        await using var context = postgres.NewContext();
        var latest = await new DiscountRuleRepository(context).GetLatestAsync(id, Token);

        latest.ShouldNotBeNull().Version.ShouldBe(2);
        latest.IsActive.ShouldBeFalse();
        latest.Type.ShouldBe("Percentage");
        latest.Parameters.GetProperty("percent").GetDecimal().ShouldBe(15m);
        (await new DiscountRuleRepository(context).GetLatestAsync(Guid.NewGuid(), Token)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListLatestAsync_ReturnsOneRowPerRuleAndFiltersOnTheLatestVersion()
    {
        var edited = Guid.NewGuid();
        var deactivated = Guid.NewGuid();
        await Insert(Rule(edited, 1, percent: 5, isActive: false));
        await Insert(Rule(edited, 2, percent: 5));
        await Insert(Rule(deactivated, 1, percent: 5));
        await Insert(Rule(deactivated, 2, percent: 5, isActive: false));

        await using var context = postgres.NewContext();
        var repository = new DiscountRuleRepository(context);
        var all = (await repository.ListLatestAsync(null, Token)).Where(rule => rule.Id == edited || rule.Id == deactivated).ToList();
        var active = (await repository.ListLatestAsync(true, Token)).Select(rule => rule.Id).ToList();
        var inactive = (await repository.ListLatestAsync(false, Token)).Select(rule => rule.Id).ToList();

        all.Count.ShouldBe(2);
        all.Single(rule => rule.Id == edited).Version.ShouldBe(2);
        active.ShouldContain(edited);
        active.ShouldNotContain(deactivated);
        inactive.ShouldContain(deactivated);
        inactive.ShouldNotContain(edited);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AddVersionAsync_SameVersionTwice_SecondReturnsFalseAndTheContextStaysUsable()
    {
        var id = Guid.NewGuid();
        var rule = new DiscountRuleDto(
            id, 1, "Rule", "Percentage", System.Text.Json.JsonDocument.Parse("""{ "percent": 5, "appliesTo": "order" }""").RootElement.Clone(),
            System.Text.Json.JsonDocument.Parse("[]").RootElement.Clone(), 1, false, false, _now, null, true, "admin-1", _now);
        await using var winner = postgres.NewContext();
        await using var loser = postgres.NewContext();

        var first = await new DiscountRuleRepository(winner).AddVersionAsync(rule, Token);
        var repository = new DiscountRuleRepository(loser);
        var second = await repository.AddVersionAsync(rule, Token);
        var next = await repository.AddVersionAsync(rule with { Version = 2 }, Token);

        first.ShouldBeTrue();
        second.ShouldBeFalse();
        next.ShouldBeTrue();
        (await repository.GetLatestAsync(id, Token))!.Version.ShouldBe(2);
    }

    private async Task<IReadOnlyList<DiscountRule>> Active()
    {
        await using var context = postgres.NewContext();

        return await new DiscountRuleRepository(context).GetActiveAsync(Token);
    }

    private async Task Insert(DiscountRuleRecord record)
    {
        await using var context = postgres.NewContext();
        context.DiscountRules.Add(record);
        await context.SaveChangesAsync(Token);
    }

    private async Task<Guid> InsertOrder(Guid clientId)
    {
        var id = Guid.NewGuid();
        var order = Order.Place(
            id,
            OrderNumbers.For(id, _now),
            ClientRef.Create(clientId, "Ana"),
            clientId.ToString(),
            OrderChannel.Shop,
            [OrderItem.Create("KB-1", "Keyboard", Money.Create(10m, Currency.Create("BRL")), 1, 1)],
            _now);
        await using var context = postgres.NewContext();
        context.Orders.Add(order.ToRecord());
        await context.SaveChangesAsync(Token);

        return id;
    }

    private async Task Redeem(Guid rule, Guid client, Guid order, bool oncePerClient)
    {
        await using var context = postgres.NewContext();
        context.DiscountRedemptions.Add(new DiscountRedemptionRecord
        {
            DiscountRuleId = rule,
            ClientId = client,
            OrderId = order,
            OncePerClient = oncePerClient,
            RedeemedAt = _now,
        });
        await context.SaveChangesAsync(Token);
    }

    private static DiscountRuleRecord Rule(Guid id, int version, decimal percent, bool isActive = true, bool oncePerClient = false) => new()
    {
        Id = id,
        Version = version,
        Name = $"Rule v{version}",
        Type = DiscountType.Percentage,
        Parameters = $$"""{ "percent": {{percent}}, "appliesTo": "order" }""",
        Conditions = "[]",
        Priority = 10,
        OncePerClient = oncePerClient,
        ValidFrom = _now,
        IsActive = isActive,
        CreatedBy = "admin-1",
        CreatedAt = _now,
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
