using System.Text.Json;
using Orders.Domain.Discounts;

namespace Orders.Domain.UnitTests;

public sealed class DiscountEngineTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DiscountClient _client = new(OrderBuilder.ClientId, new HashSet<string>(), OrderChannel.Shop, IsFirstOrder: false);

    private readonly DiscountEngine _engine = new(new FixedClock(_now));

    [Fact]
    [Trait("Category", "Unit")]
    public void Evaluate_NoRules_TakesNothingOff()
    {
        var outcome = _engine.Evaluate([], Cart(), _client);

        outcome.Total.ShouldBe(Brl(0m));
        outcome.Applied.ShouldBeEmpty();
        outcome.Lines.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Evaluate_SeveralNonStackableRules_AppliesOnlyTheBestOne()
    {
        var small = Rule(Percent(5), priority: 1);
        var best = Rule(Percent(10), priority: 50);

        var outcome = _engine.Evaluate([small, best], Cart(), _client);

        outcome.Applied.ShouldHaveSingleItem().RuleId.ShouldBe(best.Id);
        outcome.Total.ShouldBe(Brl(10m));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Evaluate_NonStackableTie_LowerPriorityThenLowerIdWins()
    {
        var first = Rule(Fixed(10), priority: 5, id: Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var lowerId = Rule(Fixed(10), priority: 5, id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var priorityOne = Rule(Fixed(10), priority: 1, id: Guid.Parse("00000000-0000-0000-0000-000000000003"));

        _engine.Evaluate([first, lowerId], Cart(), _client).Applied.Single().RuleId.ShouldBe(lowerId.Id);
        _engine.Evaluate([first, lowerId, priorityOne], Cart(), _client).Applied.Single().RuleId.ShouldBe(priorityOne.Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Evaluate_StackableRules_ApplyAfterTheBestNonStackableInPriorityOrder()
    {
        var best = Rule(Percent(10), priority: 99);
        var late = Rule(Fixed(3), priority: 20, stackable: true);
        var early = Rule(Fixed(2), priority: 10, stackable: true);

        var outcome = _engine.Evaluate([late, best, early], Cart(), _client);

        outcome.Applied.Select(applied => applied.RuleId).ShouldBe([best.Id, early.Id, late.Id]);
        outcome.Applied.Select(applied => applied.Amount.Amount).ShouldBe([10m, 2m, 3m]);
        outcome.Total.ShouldBe(Brl(15m));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Evaluate_StackablePriorityTie_IsBrokenByRuleId()
    {
        var second = Rule(Fixed(1), priority: 1, stackable: true, id: Guid.Parse("00000000-0000-0000-0000-00000000000b"));
        var first = Rule(Fixed(1), priority: 1, stackable: true, id: Guid.Parse("00000000-0000-0000-0000-00000000000a"));

        _engine.Evaluate([second, first], Cart(), _client).Applied.Select(applied => applied.RuleId).ShouldBe([first.Id, second.Id]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Evaluate_DiscountsAboveTheCap_AreCutAtMaxDiscountPercent()
    {
        var big = Rule(Percent(40), priority: 1, stackable: true);
        var more = Rule(Percent(30), priority: 2, stackable: true);
        var ignored = Rule(Fixed(1), priority: 3, stackable: true);

        var outcome = _engine.Evaluate([big, more, ignored], Cart(), _client);

        outcome.Total.ShouldBe(Brl(50m));
        outcome.Applied.Select(applied => (applied.RuleId, applied.Amount.Amount)).ShouldBe([(big.Id, 40m), (more.Id, 10m)]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Evaluate_CustomCap_IsUsed()
    {
        var engine = new DiscountEngine(new FixedClock(_now), maxDiscountPercent: 5m);

        engine.Evaluate([Rule(Percent(10))], Cart(), _client).Total.ShouldBe(Brl(5m));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(-1)]
    [InlineData(100.01)]
    public void Constructor_CapOutsideZeroToHundred_Throws(decimal cap)
    {
        Should.Throw<DomainException>(() => new DiscountEngine(new FixedClock(_now), cap));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Evaluate_OrderLevelDiscount_IsSpreadOverTheLinesAndAddsUp()
    {
        // 10.00 off 100.00 split 2/3 : 1/3 -> 6.67 + 3.33, the extra cent going to the larger remainder.
        var cart = DiscountCart.Create(OrderBuilder.Brl, [Line("A-1", 66.67m, 1), Line("B-1", 33.33m, 1)]);

        var outcome = _engine.Evaluate([Rule(Fixed(10))], cart, _client);

        outcome.LineDiscountFor("A-1").ShouldBe(Brl(6.67m));
        outcome.LineDiscountFor("B-1").ShouldBe(Brl(3.33m));
        outcome.Lines.Sum(line => line.Amount.Amount).ShouldBe(outcome.Total.Amount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Evaluate_OddCentsSpread_AlwaysSumToTheRuleAmount()
    {
        var cart = DiscountCart.Create(OrderBuilder.Brl, [Line("A-1", 1m, 1), Line("B-1", 1m, 1), Line("C-1", 1m, 1)]);

        var outcome = _engine.Evaluate([Rule(Fixed(1))], cart, _client);

        outcome.Lines.Select(line => line.Amount.Amount).ShouldBe([0.34m, 0.33m, 0.33m]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Evaluate_StackedLineDiscounts_NeverExceedALine()
    {
        var engine = new DiscountEngine(new FixedClock(_now), maxDiscountPercent: 100m);
        var free = Rule(Parameters(DiscountType.BuyXGetY, new { buy = 1, get = 1 }), stackable: true, priority: 1);
        var tenPercent = Rule(Parameters(DiscountType.Percentage, new { percent = 10, appliesTo = "line" }), stackable: true, priority: 2);
        var cart = DiscountCart.Create(OrderBuilder.Brl, [Line("A-1", 10m, 2)]);

        var outcome = engine.Evaluate([free, tenPercent], cart, _client);

        outcome.LineDiscountFor("A-1").ShouldBe(Brl(12m));
        outcome.Applied.Select(applied => applied.Amount.Amount).ShouldBe([10m, 2m]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Evaluate_RuleWhoseConditionsFail_IsSkipped()
    {
        var rule = Rule(Percent(10), conditions: """[{ "kind": "MinSubtotal", "amount": 1000, "currency": "BRL" }]""");

        _engine.Evaluate([rule], Cart(), _client).Applied.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Evaluate_RuleConditions_SeeTheClient()
    {
        var rule = Rule(Percent(10), conditions: """[{ "kind": "ChannelIs", "channel": "admin" }]""");

        _engine.Evaluate([rule], Cart(), _client with { Channel = OrderChannel.Admin }).Applied.ShouldHaveSingleItem();
        _engine.Evaluate([rule], Cart(), _client).Applied.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Evaluate_InactiveOrOutOfDateRules_AreSkipped()
    {
        var inactive = Rule(Percent(10), isActive: false);
        var future = Rule(Percent(10), validFrom: _now.AddSeconds(1));
        var expired = Rule(Percent(10), validTo: _now);
        var current = Rule(Percent(1), validFrom: _now, validTo: _now.AddDays(1));

        _engine.Evaluate([inactive, future, expired, current], Cart(), _client).Applied.ShouldHaveSingleItem().RuleId.ShouldBe(current.Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Evaluate_RuleThatTakesNothingOff_IsNotApplied()
    {
        var usdOnly = Rule(Parameters(DiscountType.FixedAmount, new { amounts = new[] { new { currency = "USD", amount = 5 } } }));

        _engine.Evaluate([usdOnly], Cart(), _client).Applied.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Evaluate_AppliedRule_CarriesItsIdVersionAndOncePerClient()
    {
        var rule = Rule(Percent(10), oncePerClient: true, version: 4);

        var applied = _engine.Evaluate([rule], Cart(), _client).Applied.Single();

        applied.ShouldBe(new AppliedDiscount(rule.Id, 4, Brl(10m), OncePerClient: true));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Evaluate_SameInputHundredTimes_GivesTheSameOutput()
    {
        DiscountRule[] rules =
        [
            Rule(Percent(7), priority: 3),
            Rule(Fixed(4), priority: 3),
            Rule(Parameters(DiscountType.BuyXGetY, new { buy = 2, get = 1 }), priority: 2, stackable: true),
            Rule(Percent(3), priority: 2, stackable: true),
        ];
        var cart = DiscountCart.Create(OrderBuilder.Brl, [Line("A-1", 19.99m, 3), Line("B-1", 7.33m, 2), Line("C-1", 0.99m, 5)]);
        var expected = Describe(_engine.Evaluate(rules, cart, _client));

        for (var run = 0; run < 100; run++)
        {
            Describe(_engine.Evaluate(rules.Reverse().ToArray(), cart, _client)).ShouldBe(expected);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DiscountRule_Create_InvalidParametersConditionsOrDates_Throw()
    {
        Should.Throw<DomainException>(() => Rule("""{ "percent": 0, "appliesTo": "order" }"""));
        Should.Throw<DomainException>(() => Rule(Percent(10), conditions: """[{ "kind": "Nope" }]"""));
        Should.Throw<DomainException>(() => Rule(Percent(10), validFrom: _now, validTo: _now));
        Should.Throw<DomainException>(() => Rule(Percent(10), version: 0));
        Should.Throw<DomainException>(() => Rule(Percent(10), id: Guid.Empty));
        Should.Throw<DomainException>(() => DiscountRule.Create(Guid.NewGuid(), 1, " ", DiscountType.Percentage, Percent(1), "[]", 1, false, false, _now, null, true));
    }

    private static string Describe(DiscountOutcome outcome) =>
        string.Join(";", outcome.Applied.Select(applied => $"{applied.RuleId}:{applied.Amount.Amount}"))
        + "|" + string.Join(";", outcome.Lines.Select(line => $"{line.Sku}:{line.Amount.Amount}"));

    private static DiscountRule Rule(
        string parameters,
        int priority = 10,
        bool stackable = false,
        Guid? id = null,
        string conditions = "[]",
        bool isActive = true,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validTo = null,
        bool oncePerClient = false,
        int version = 1,
        DiscountType? type = null)
    {
        var json = JsonDocument.Parse(parameters).RootElement;
        var ruleType = type ?? (json.TryGetProperty("amounts", out _) ? DiscountType.FixedAmount
            : json.TryGetProperty("buy", out _) ? DiscountType.BuyXGetY
            : DiscountType.Percentage);

        return DiscountRule.Create(
            id ?? Guid.NewGuid(), version, "rule", ruleType, parameters, conditions, priority, stackable, oncePerClient, validFrom ?? _now.AddDays(-1), validTo, isActive);
    }

    private static string Percent(decimal percent) => Parameters(DiscountType.Percentage, new { percent, appliesTo = "order" });

    private static string Fixed(decimal amount) => Parameters(DiscountType.FixedAmount, new { amounts = new[] { new { currency = "BRL", amount } } });

    private static string Parameters(DiscountType type, object value) => JsonSerializer.Serialize(value);

    /// <summary>Two lines, 100.00 in all.</summary>
    private static DiscountCart Cart() => DiscountCart.Create(OrderBuilder.Brl, [Line("KB-1", 40m, 2), Line("MS-1", 20m, 1)]);

    private static DiscountLine Line(string sku, decimal price, int quantity) => new(sku, "peripherals", Brl(price), quantity);

    private static Money Brl(decimal amount) => OrderBuilder.BrlAmount(amount);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
