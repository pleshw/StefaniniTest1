namespace Catalog.Domain.UnitTests;

internal sealed class ProductBuilder
{
    private string _name = "Keyboard";
    private string _category = "peripherals";
    private IEnumerable<Money> _prices = [Brl(100m)];

    public ProductBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public ProductBuilder WithCategory(string category)
    {
        _category = category;
        return this;
    }

    public ProductBuilder WithPrices(params Money[] prices)
    {
        _prices = prices;
        return this;
    }

    public Product Build() => Product.Create(
        Sku.Create("SKU-1"),
        _name,
        "A keyboard",
        _category,
        new Dictionary<string, string> { ["layout"] = "abnt2" },
        _prices);

    public static Money Brl(decimal amount) => Money.Create(amount, Currency.Create("BRL"));

    public static Money Usd(decimal amount) => Money.Create(amount, Currency.Create("USD"));
}
