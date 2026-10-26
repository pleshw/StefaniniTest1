namespace Catalog.Domain;

public sealed class Product
{
    private readonly List<Money> _prices;
    private readonly List<IDomainEvent> _domainEvents = [];

    private Product(
        Sku sku,
        string name,
        string description,
        string category,
        IReadOnlyDictionary<string, string> attributes,
        List<Money> prices)
    {
        Sku = sku;
        Name = name;
        Description = description;
        Category = category;
        Attributes = attributes;
        _prices = prices;
        IsActive = true;
        Version = 1;
    }

    public Sku Sku { get; }

    public string Name { get; private set; }

    public string Description { get; }

    public string Category { get; }

    public IReadOnlyDictionary<string, string> Attributes { get; }

    public IReadOnlyList<Money> Prices => _prices;

    public bool IsActive { get; private set; }

    public int Version { get; private set; }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public static Product Create(
        Sku sku,
        string? name,
        string? description,
        string? category,
        IReadOnlyDictionary<string, string>? attributes,
        IEnumerable<Money> prices)
    {
        var priceList = prices.ToList();

        if (priceList.Select(price => price.Currency).Distinct().Count() != priceList.Count)
        {
            throw new DomainException("A product can have only one price per currency.");
        }

        return new Product(
            sku,
            RequireText(name, "Name"),
            description?.Trim() ?? string.Empty,
            RequireText(category, "Category"),
            attributes is null ? new Dictionary<string, string>() : new Dictionary<string, string>(attributes),
            priceList);
    }

    public void ChangePrice(Money newPrice)
    {
        if (!IsActive)
        {
            throw new DomainException("A deactivated product cannot change price.");
        }

        var index = _prices.FindIndex(price => price.Currency == newPrice.Currency);
        var oldPrice = index >= 0 ? _prices[index] : null;

        if (oldPrice == newPrice)
        {
            return;
        }

        if (index >= 0)
        {
            _prices[index] = newPrice;
        }
        else
        {
            _prices.Add(newPrice);
        }

        Version++;
        _domainEvents.Add(new PriceChanged(Sku, oldPrice, newPrice, Version));
    }

    public void Rename(string? name)
    {
        var newName = RequireText(name, "Name");

        if (newName == Name)
        {
            return;
        }

        Name = newName;
        Version++;
    }

    public void Deactivate()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        Version++;
    }

    public void ClearDomainEvents() => _domainEvents.Clear();

    private static string RequireText(string? value, string field)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed)
            ? throw new DomainException($"{field} must not be empty.")
            : trimmed;
    }
}
