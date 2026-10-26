namespace Catalog.Domain;

/// <param name="OldPrice">The previous price in that currency, or null when the product had none.</param>
public sealed record PriceChanged(Sku Sku, Money? OldPrice, Money NewPrice, int Version) : IDomainEvent;
