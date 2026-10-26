using Orders.Domain.Discounts;

namespace Orders.Domain.UnitTests;

public sealed class DiscountRuleCurrencyTests
{
    private static readonly DateTimeOffset _from = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(DiscountType.Percentage, """{ "percent": 5, "appliesTo": "order" }""", "[]", true)]
    [InlineData(DiscountType.BuyXGetY, """{ "buy": 1, "get": 1 }""", "[]", true)]
    [InlineData(DiscountType.FixedAmount, """{ "amounts": [ { "currency": "BRL", "amount": 5 } ] }""", "[]", true)]
    [InlineData(DiscountType.FixedAmount, """{ "amounts": [ { "currency": "USD", "amount": 5 } ] }""", "[]", false)]
    [InlineData(DiscountType.Tiered, """{ "currency": "brl", "tiers": [ { "minSubtotal": 0, "percent": 5 } ] }""", "[]", true)]
    [InlineData(DiscountType.Tiered, """{ "currency": "USD", "tiers": [ { "minSubtotal": 0, "percent": 5 } ] }""", "[]", false)]
    [InlineData(DiscountType.Percentage, """{ "percent": 5, "appliesTo": "order" }""", """[{ "kind": "MinSubtotal", "amount": 1, "currency": "USD" }]""", false)]
    [InlineData(DiscountType.Percentage, """{ "percent": 5, "appliesTo": "order" }""", """[{ "kind": "MinSubtotal", "amount": 1, "currency": "BRL" }]""", true)]
    public void CanApplyIn_Brl_DependsOnTheCurrenciesTheRuleNames(DiscountType type, string parameters, string conditions, bool expected)
    {
        var rule = DiscountRule.Create(Guid.NewGuid(), 1, "rule", type, parameters, conditions, 1, false, false, _from, null, true);

        rule.CanApplyIn(OrderBuilder.Brl).ShouldBe(expected);
    }
}
