using Microsoft.Extensions.Caching.Memory;
using Orders.Application.Confirmation;
using Orders.Domain.Confirmation;
using Orders.Infrastructure.Persistence;

namespace Orders.IntegrationTests;

/// <summary>The active confirmation rules cache over a substitute repository (no database).</summary>
public sealed class ActiveConfirmationRulesCacheTests : IDisposable
{
    private readonly IConfirmationRuleRepository _repository = Substitute.For<IConfirmationRuleRepository>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly CachedActiveConfirmationRules _rules;

    public ActiveConfirmationRulesCacheTests()
    {
        _repository.GetActiveAsync(Token).Returns([ConfirmationRule.Create(Guid.NewGuid(), 1, "rule", ConfirmationAction.Hold, """[{ "kind": "FirstOrderOfClient" }]""", 1, true)]);
        _rules = new CachedActiveConfirmationRules(_repository, _cache, new ConfirmationOptions());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAsync_CalledTwice_LoadsTheRulesOnceUnderOneKey()
    {
        await _rules.GetAsync(Token);
        await _rules.GetAsync(Token);

        await _repository.Received(1).GetActiveAsync(Token);
        _cache.TryGetValue(IActiveConfirmationRules.CacheKey, out _).ShouldBeTrue();
        IActiveConfirmationRules.CacheKey.ShouldBe("confirmation-rules:active");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Invalidate_AfterAChange_NextCallReloads()
    {
        await _rules.GetAsync(Token);

        _rules.Invalidate();
        await _rules.GetAsync(Token);

        await _repository.Received(2).GetActiveAsync(Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAsync_EntryOlderThanTheCacheDuration_Reloads()
    {
        var shortLived = new CachedActiveConfirmationRules(_repository, _cache, new ConfirmationOptions { CacheDuration = TimeSpan.FromMilliseconds(50) });
        await shortLived.GetAsync(Token);

        await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        await shortLived.GetAsync(Token);

        await _repository.Received(2).GetActiveAsync(Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ConfirmationOptions_Defaults_AreNoSeedAndFiveSeconds()
    {
        var options = new ConfirmationOptions();

        options.SeedEnabled.ShouldBeFalse();
        options.CacheDuration.ShouldBe(TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _cache.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
