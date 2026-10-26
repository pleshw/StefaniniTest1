using Catalog.Domain.Validation;
using FluentValidation;

namespace Catalog.Domain;

public sealed class Product
{
    private static readonly ContentValidator _contentValidator = new();

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
        _contentValidator.EnsureValid(new Content(name, category, priceList));

        return new Product(
            sku,
            name!.Trim(),
            description?.Trim() ?? string.Empty,
            category!.Trim(),
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
        var newPrices = prices.ToList();
        _contentValidator.EnsureValid(new Content(name, category, newPrices));

        var newName = name!.Trim();
        var newCategory = category!.Trim();
        var newDescription = description?.Trim() ?? string.Empty;
        var newAttributes = attributes is null ? new Dictionary<string, string>() : new Dictionary<string, string>(attributes);

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
        _contentValidator.EnsureValid(new Content(name, Category, _prices));
        var newName = name!.Trim();

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

    private bool SameAttributes(Dictionary<string, string> other) =>
        other.Count == Attributes.Count
        && other.All(pair => Attributes.TryGetValue(pair.Key, out var value) && value == pair.Value);

    /// <summary>The caller-supplied content checked before a product is created or changed.</summary>
    private sealed record Content(string? Name, string? Category, IReadOnlyList<Money> Prices);

    private sealed class ContentValidator : AbstractValidator<Content>
    {
        public ContentValidator()
        {
            RuleFor(content => content.Name).NotEmpty().WithMessage("Name must not be empty.");
            RuleFor(content => content.Category).NotEmpty().WithMessage("Category must not be empty.");
            RuleFor(content => content.Prices)
                .Must(prices => prices.Select(price => price.Currency).Distinct().Count() == prices.Count)
                .WithMessage("A product can have only one price per currency.");
        }
    }
}
