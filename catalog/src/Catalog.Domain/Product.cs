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

    public string Description { get; private set; }

    public string Category { get; private set; }

    public IReadOnlyDictionary<string, string> Attributes { get; private set; }

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
        EnsureOnePricePerCurrency(priceList);

        return new Product(
            sku,
            RequireText(name, "Name"),
            description?.Trim() ?? string.Empty,
            RequireText(category, "Category"),
            attributes is null ? new Dictionary<string, string>() : new Dictionary<string, string>(attributes),
            priceList);
    }

    /// <summary>Rebuilds a stored product; the same invariants as <see cref="Create"/> apply.</summary>
    public static Product Restore(
        Sku sku,
        string name,
        string description,
        string category,
        IReadOnlyDictionary<string, string> attributes,
        IEnumerable<Money> prices,
        bool isActive,
        int version)
    {
        if (version < 1)
        {
            throw new DomainException("Version must be at least 1.");
        }

        var product = Create(sku, name, description, category, attributes, prices);
        product.IsActive = isActive;
        product.Version = version;
        return product;
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

    /// <summary>
    /// Replaces the editable content in one step: one version bump, and a <see cref="PriceChanged"/> for every added
    /// or changed price. A price left out is dropped without an event. Nothing changes when the input is invalid.
    /// </summary>
    public void Replace(
        string? name,
        string? description,
        string? category,
        IReadOnlyDictionary<string, string>? attributes,
        IEnumerable<Money> prices)
    {
        var newName = RequireText(name, "Name");
        var newCategory = RequireText(category, "Category");
        var newDescription = description?.Trim() ?? string.Empty;
        var newAttributes = attributes is null ? new Dictionary<string, string>() : new Dictionary<string, string>(attributes);
        var newPrices = prices.ToList();
        EnsureOnePricePerCurrency(newPrices);

        var changedPrices = newPrices
            .Select(price => (Old: _prices.Find(current => current.Currency == price.Currency), New: price))
            .Where(pair => pair.Old != pair.New)
            .ToList();
        var priceRemoved = _prices.Exists(current => newPrices.TrueForAll(price => price.Currency != current.Currency));

        if (!IsActive && (changedPrices.Count > 0 || priceRemoved))
        {
            throw new DomainException("A deactivated product cannot change price.");
        }

        var contentChanged = newName != Name
            || newDescription != Description
            || newCategory != Category
            || !SameAttributes(newAttributes);

        if (!contentChanged && changedPrices.Count == 0 && !priceRemoved)
        {
            return;
        }

        Name = newName;
        Description = newDescription;
        Category = newCategory;
        Attributes = newAttributes;
        _prices.Clear();
        _prices.AddRange(newPrices);
        Version++;
        _domainEvents.AddRange(changedPrices.Select(pair => new PriceChanged(Sku, pair.Old, pair.New, Version)));
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

    private static void EnsureOnePricePerCurrency(List<Money> prices)
    {
        if (prices.Select(price => price.Currency).Distinct().Count() != prices.Count)
        {
            throw new DomainException("A product can have only one price per currency.");
        }
    }

    private bool SameAttributes(Dictionary<string, string> other) =>
        other.Count == Attributes.Count
        && other.All(pair => Attributes.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static string RequireText(string? value, string field)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed)
            ? throw new DomainException($"{field} must not be empty.")
            : trimmed;
    }
}
