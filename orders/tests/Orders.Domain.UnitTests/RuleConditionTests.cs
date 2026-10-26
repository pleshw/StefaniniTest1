using Orders.Domain.Discounts;
using Orders.Domain.Rules;

namespace Orders.Domain.UnitTests;

public sealed class RuleConditionTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("""[{ "kind": "MinSubtotal", "amount": 49.98, "currency": "BRL" }]""", true)]
    [InlineData("""[{ "kind": "MinSubtotal", "amount": 49.99, "currency": "BRL" }]""", false)]
    [InlineData("""[{ "kind": "MinSubtotal", "amount": 1, "currency": "USD" }]""", false)]
    [InlineData("""[{ "kind": "SkuIn", "skus": ["ms-1", "XX-9"] }]""", true)]
    [InlineData("""[{ "kind": "SkuIn", "skus": ["XX-9"] }]""", false)]
    [InlineData("""[{ "kind": "CategoryIn", "categories": ["Peripherals"] }]""", true)]
    [InlineData("""[{ "kind": "CategoryIn", "categories": ["books"] }]""", false)]
    [InlineData("""[{ "kind": "MinQuantity", "quantity": 3 }]""", true)]
    [InlineData("""[{ "kind": "MinQuantity", "quantity": 4 }]""", false)]
    [InlineData("""[{ "kind": "MinQuantity", "quantity": 2, "skus": ["KB-1"] }]""", true)]
    [InlineData("""[{ "kind": "MinQuantity", "quantity": 2, "skus": ["MS-1"] }]""", false)]
    [InlineData("""[{ "kind": "ClientSegment", "segments": ["VIP"] }]""", true)]
    [InlineData("""[{ "kind": "ClientSegment", "segments": ["wholesale"] }]""", false)]
    [InlineData("""[{ "kind": "DateWindow", "from": "2026-06-15T12:00:00Z", "to": "2026-06-16T00:00:00Z" }]""", true)]
    [InlineData("""[{ "kind": "DateWindow", "from": "2026-06-15T12:00:01Z" }]""", false)]
    [InlineData("""[{ "kind": "DateWindow", "to": "2026-06-15T12:00:00Z" }]""", false)]
    [InlineData("""[{ "kind": "DateWindow", "to": "2026-06-15T12:00:01Z" }]""", true)]
    [InlineData("""[{ "kind": "TotalAbove", "amount": 49.97, "currency": "BRL" }]""", true)]
    [InlineData("""[{ "kind": "TotalAbove", "amount": 49.98, "currency": "BRL" }]""", false)]
    [InlineData("""[{ "kind": "TotalAbove", "amount": 1, "currency": "USD" }]""", false)]
    [InlineData("""[{ "kind": "FirstOrderOfClient" }]""", true)]
    [InlineData("""[{ "kind": "ChannelIs", "channel": "shop" }]""", true)]
    [InlineData("""[{ "kind": "ChannelIs", "channel": "admin" }]""", false)]
    public void IsSatisfiedBy_EachCondition_MatchesTheContext(string conditions, bool expected)
    {
        RuleConditions.Create(conditions).IsSatisfiedBy(Context()).ShouldBe(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FirstOrderOfClient_ClientWithEarlierOrders_IsFalse()
    {
        RuleConditions.Create("""[{ "kind": "FirstOrderOfClient" }]""").IsSatisfiedBy(Context() with { IsFirstOrderOfClient = false }).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsSatisfiedBy_SeveralConditions_AllMustHold()
    {
        var conditions = RuleConditions.Create("""
            [
              { "kind": "MinSubtotal", "amount": 10, "currency": "BRL" },
              { "kind": "ChannelIs", "channel": "shop" },
              { "kind": "SkuIn", "skus": ["KB-1"] }
            ]
            """);

        conditions.Items.Count.ShouldBe(3);
        conditions.IsSatisfiedBy(Context()).ShouldBeTrue();
        conditions.IsSatisfiedBy(Context() with { Channel = OrderChannel.Admin }).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsSatisfiedBy_NoConditions_AlwaysHolds()
    {
        RuleConditions.Create("[]").IsSatisfiedBy(Context()).ShouldBeTrue();
        RuleConditions.None.IsSatisfiedBy(Context()).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AllOf_Specifications_CombineWithAnd()
    {
        ISpecification<int> positive = new Is<int>(value => value > 0);
        ISpecification<int> even = new Is<int>(value => value % 2 == 0);

        var both = new AllOf<int>([positive, even]);

        both.IsSatisfiedBy(4).ShouldBeTrue();
        both.IsSatisfiedBy(3).ShouldBeFalse();
        both.IsSatisfiedBy(-2).ShouldBeFalse();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("""[{ "kind": "Weekday", "day": "monday" }]""")]
    [InlineData("""[{ "skus": ["KB-1"] }]""")]
    [InlineData("""[{ "kind": "SkuIn", "skus": ["KB-1"], "extra": true }]""")]
    [InlineData("""[{ "kind": "SkuIn", "skus": [] }]""")]
    [InlineData("""[{ "kind": "SkuIn" }]""")]
    [InlineData("""[{ "kind": "CategoryIn", "categories": [""] }]""")]
    [InlineData("""[{ "kind": "MinSubtotal", "amount": -1, "currency": "BRL" }]""")]
    [InlineData("""[{ "kind": "MinSubtotal", "amount": 1, "currency": "REAL" }]""")]
    [InlineData("""[{ "kind": "MinQuantity", "quantity": 0 }]""")]
    [InlineData("""[{ "kind": "ClientSegment", "segments": [] }]""")]
    [InlineData("""[{ "kind": "DateWindow" }]""")]
    [InlineData("""[{ "kind": "DateWindow", "from": "2026-06-16T00:00:00Z", "to": "2026-06-15T00:00:00Z" }]""")]
    [InlineData("""[{ "kind": "TotalAbove", "amount": 1 }]""")]
    [InlineData("""[{ "kind": "ChannelIs", "channel": "phone" }]""")]
    [InlineData("""[{ "kind": "FirstOrderOfClient", "orders": 1 }]""")]
    [InlineData("""{ "kind": "SkuIn", "skus": ["KB-1"] }""")]
    [InlineData("""[null]""")]
    [InlineData("null")]
    [InlineData("not json")]
    public void Validate_InvalidConditions_ReportsAndCreateThrows(string conditions)
    {
        RuleConditions.Validate(conditions).IsValid.ShouldBeFalse();
        Should.Throw<DomainException>(() => RuleConditions.Create(conditions));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Validate_KindAfterTheOtherProperties_IsAccepted()
    {
        RuleConditions.Validate("""[{ "skus": ["KB-1"], "kind": "SkuIn" }]""").IsValid.ShouldBeTrue();
    }

    private static RuleContext Context()
    {
        var brl = OrderBuilder.Brl;
        var cart = DiscountCart.Create(brl, [
            new DiscountLine("KB-1", "peripherals", Money.Create(19.99m, brl), 2),
            new DiscountLine("MS-1", "peripherals", Money.Create(10m, brl), 1),
        ]);

        return new RuleContext(cart, OrderBuilder.ClientId, new HashSet<string>(["vip"]), OrderChannel.Shop, IsFirstOrderOfClient: true, cart.Subtotal, _now);
    }

    private sealed class Is<T>(Func<T, bool> predicate) : ISpecification<T>
    {
        public bool IsSatisfiedBy(T candidate) => predicate(candidate);
    }
}
