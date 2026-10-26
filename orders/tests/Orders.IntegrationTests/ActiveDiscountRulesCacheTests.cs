using Microsoft.Extensions.Caching.Memory;
using Orders.Application.Discounts;
using Orders.Domain;
using Orders.Domain.Discounts;
using Orders.Infrastructure.Persistence;

namespace Orders.IntegrationTests;

/// <summary>The active-rules cache over a substitute repository (no database).</summary>
public sealed class ActiveDiscountRulesCacheTests : IDisposable
{
    private static readonly DateTimeOffset _from = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IDiscountRuleRepository _repository = Substitute.For<IDiscountRuleRepository>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly CachedActiveDiscountRules _rules;

    public ActiveDiscountRulesCacheTests()
    {
        _repository.GetActiveAsync(Token).Returns([
            Rule(DiscountType.Percentage, """{ "percent": 10, "appliesTo": "order" }"""),
            Rule(DiscountType.FixedAmount, """{ "amounts": [ { "currency": "USD", "amount": 5 } ] }"""),
        ]);
        _rules = new CachedActiveDiscountRules(_repository, _cache, new DiscountOptions());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAsync_CalledTwice_LoadsTheRulesOnceUnderOneKey()
    {
        await _rules.GetAsync(Currency.Create("BRL"), Token);
        await _rules.GetAsync(Currency.Create("USD"), Token);

        await _repository.Received(1).GetActiveAsync(Token);
        _cache.TryGetValue(IActiveDiscountRules.CacheKey, out _).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAsync_Currency_ReturnsOnlyRulesThatCanApplyInIt()
    {
        (await _rules.GetAsync(Currency.Create("BRL"), Token)).ShouldHaveSingleItem().Type.ShouldBe(DiscountType.Percentage);
        (await _rules.GetAsync(Currency.Create("USD"), Token)).Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Invalidate_AfterAChange_NextCallReloads()
    {
        await _rules.GetAsync(Currency.Create("BRL"), Token);

        _rules.Invalidate();
        await _rules.GetAsync(Currency.Create("BRL"), Token);

        await _repository.Received(2).GetActiveAsync(Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAsync_EntryOlderThanTheCacheDuration_Reloads()
    {
        var shortLived = new CachedActiveDiscountRules(_repository, _cache, new DiscountOptions { CacheDuration = TimeSpan.FromMilliseconds(50) });
        await shortLived.GetAsync(Currency.Create("BRL"), Token);

        await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        await shortLived.GetAsync(Currency.Create("BRL"), Token);

        await _repository.Received(2).GetActiveAsync(Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DiscountOptions_Defaults_AreSixtySecondsAndFiftyPercent()
    {
        var options = new DiscountOptions();

        options.CacheDuration.ShouldBe(TimeSpan.FromSeconds(60));
        options.MaxDiscountPercent.ShouldBe(50m);
    }

    public void Dispose() => _cache.Dispose();

    private static DiscountRule Rule(DiscountType type, string parameters) =>
        DiscountRule.Create(Guid.NewGuid(), 1, "rule", type, parameters, "[]", 1, false, false, _from, null, true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
