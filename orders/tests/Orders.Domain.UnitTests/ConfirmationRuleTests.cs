using Orders.Domain.Confirmation;
using Orders.Domain.Discounts;
using Orders.Domain.Rules;

namespace Orders.Domain.UnitTests;

public sealed class ConfirmationRuleTests
{
    private const string _large = """[{ "kind": "TotalAbove", "amount": 5000, "currency": "BRL" }]""";

    private static readonly Currency _brl = Currency.Create("BRL");
    private static readonly DateTimeOffset _now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_ValidRule_KeepsTheValues()
    {
        var id = Guid.NewGuid();

        var rule = ConfirmationRule.Create(id, 2, "  Large BRL order  ", ConfirmationAction.Hold, _large, 5, true);

        rule.Id.ShouldBe(id);
        rule.Version.ShouldBe(2);
        rule.Name.ShouldBe("Large BRL order");
        rule.Action.ShouldBe(ConfirmationAction.Hold);
        rule.Priority.ShouldBe(5);
        rule.IsActive.ShouldBeTrue();
        rule.Conditions.Items.ShouldHaveSingleItem().ShouldBeOfType<TotalAboveCondition>();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null, "Name")]
    [InlineData(" ", "Name")]
    public void Create_BlankName_Throws(string? name, string _)
    {
        Should.Throw<DomainException>(() => ConfirmationRule.Create(Guid.NewGuid(), 1, name, ConfirmationAction.Hold, _large, 1, true));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_EmptyIdOrVersionBelowOne_Throws()
    {
        Should.Throw<DomainException>(() => ConfirmationRule.Create(Guid.Empty, 1, "x", ConfirmationAction.Hold, _large, 1, true));
        Should.Throw<DomainException>(() => ConfirmationRule.Create(Guid.NewGuid(), 0, "x", ConfirmationAction.Hold, _large, 1, true));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("[]")]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""[{ "kind": "Nope" }]""")]
    [InlineData("""[{ "kind": "MinSubtotal", "amount": 5, "currency": "BRL" }]""")]
    [InlineData("""[{ "kind": "SkuIn", "skus": ["KB-1"] }]""")]
    [InlineData("""[{ "kind": "TotalAbove", "amount": -1, "currency": "BRL" }]""")]
    public void Create_ConditionsThatAreEmptyInvalidOrNotForConfirmation_Throw(string? conditions)
    {
        Should.Throw<DomainException>(() => ConfirmationRule.Create(Guid.NewGuid(), 1, "x", ConfirmationAction.Hold, conditions, 1, true));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateConditions_ReportsWhichKindsAreAllowed()
    {
        var result = ConfirmationRule.ValidateConditions("""[{ "kind": "CategoryIn", "categories": ["books"] }]""");

        result.IsValid.ShouldBeFalse();
        result.ToString(" ").ShouldContain("CategoryIn");
        result.ToString(" ").ShouldContain("TotalAbove");
        ConfirmationRule.ValidateConditions(_large).IsValid.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_UnknownAction_Throws()
    {
        Should.Throw<DomainException>(() => ConfirmationRule.Create(Guid.NewGuid(), 1, "x", (ConfirmationAction)99, _large, 1, true));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(5000.01, "BRL", true)]
    [InlineData(5000.00, "BRL", false)]
    [InlineData(9000, "USD", false)]
    public void Matches_TotalAbove_IsStrictlyAboveInTheRuleCurrency(double total, string currency, bool expected)
    {
        var rule = ConfirmationRule.Create(Guid.NewGuid(), 1, "Large", ConfirmationAction.Hold, _large, 1, true);

        rule.Matches(Context(Money.Create((decimal)total, Currency.Create(currency)))).ShouldBe(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Matches_InactiveRule_NeverMatches()
    {
        var rule = ConfirmationRule.Create(Guid.NewGuid(), 1, "Large", ConfirmationAction.Hold, _large, 1, isActive: false);

        rule.Matches(Context(Money.Create(9000m, _brl))).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Matches_SeveralConditions_AllMustHold()
    {
        var rule = ConfirmationRule.Create(
            Guid.NewGuid(), 1, "Large first order", ConfirmationAction.Hold,
            """[{ "kind": "TotalAbove", "amount": 100, "currency": "BRL" }, { "kind": "FirstOrderOfClient" }, { "kind": "ChannelIs", "channel": "shop" }]""", 1, true);

        rule.Matches(Context(Money.Create(200m, _brl))).ShouldBeTrue();
        rule.Matches(Context(Money.Create(200m, _brl)) with { IsFirstOrderOfClient = false }).ShouldBeFalse();
        rule.Matches(Context(Money.Create(200m, _brl)) with { Channel = OrderChannel.Admin }).ShouldBeFalse();
        rule.Matches(Context(Money.Create(100m, _brl))).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FirstMatchingHold_NoRulesOrNoMatch_IsNull()
    {
        ConfirmationRule.FirstMatchingHold([], Context(Money.Create(9000m, _brl))).ShouldBeNull();
        ConfirmationRule.FirstMatchingHold([Rule("Large", 1)], Context(Money.Create(10m, _brl))).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FirstMatchingHold_SeveralMatch_TakesTheLowestPriorityThenTheLowestId()
    {
        var low = Rule("Low", 1, Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var tieLowerId = Rule("Tie, lower id", 5, Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var tieHigherId = Rule("Tie, higher id", 5, Guid.Parse("00000000-0000-0000-0000-000000000009"));
        var context = Context(Money.Create(9000m, _brl));

        ConfirmationRule.FirstMatchingHold([tieHigherId, tieLowerId, low], context)!.Name.ShouldBe("Low");
        ConfirmationRule.FirstMatchingHold([tieHigherId, tieLowerId], context)!.Name.ShouldBe("Tie, lower id");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FirstMatchingHold_InactiveRule_IsSkipped()
    {
        var inactive = ConfirmationRule.Create(Guid.NewGuid(), 1, "Off", ConfirmationAction.Hold, _large, 1, isActive: false);

        ConfirmationRule.FirstMatchingHold([inactive, Rule("On", 9)], Context(Money.Create(9000m, _brl)))!.Name.ShouldBe("On");
    }

    private static ConfirmationRule Rule(string name, int priority, Guid? id = null) =>
        ConfirmationRule.Create(id ?? Guid.NewGuid(), 1, name, ConfirmationAction.Hold, _large, priority, true);

    private static RuleContext Context(Money total) => new(
        DiscountCart.Create(total.Currency, [new DiscountLine("KB-1", string.Empty, total, 1)]),
        Guid.NewGuid(),
        new HashSet<string>(),
        OrderChannel.Shop,
        IsFirstOrderOfClient: true,
        total,
        _now);
}
