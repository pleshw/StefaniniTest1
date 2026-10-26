using Catalog.Domain;

namespace Catalog.Application.UnitTests;

internal static class ProductTestData
{
    public static readonly CurrencyPolicy Currencies = new(["BRL", "USD"]);

    public static Product Product(string sku, string name = "Keyboard", params Money[] prices) => Domain.Product.Create(
        Sku.Create(sku),
        name,
        "A keyboard",
        "peripherals",
        new Dictionary<string, string> { ["layout"] = "abnt2" },
        prices.Length == 0 ? [Brl(100m), Usd(20m)] : prices);

    public static Money Brl(decimal amount) => Money.Create(amount, Currency.Create("BRL"));

    public static Money Usd(decimal amount) => Money.Create(amount, Currency.Create("USD"));
}
