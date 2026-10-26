namespace Orders.Domain.Discounts;

/// <summary>The fixed set of rule types; a rule's parameters are validated by the strategy of its type.</summary>
public enum DiscountType
{
    Percentage,
    FixedAmount,
    BuyXGetY,
    Tiered,
}
