using Catalog.Domain;

namespace Catalog.Infrastructure.Seeding;

/// <summary>The development catalog: 50 deterministic products, each priced in BRL and USD.</summary>
internal static class SeedProducts
{
    private const decimal _usdPerBrl = 0.18m;

    private static readonly (string Category, string Prefix, string[] Names, decimal BasePrice)[] _groups =
    [
        ("peripherals", "PER", ["Wired keyboard", "Wireless keyboard", "Mechanical keyboard", "Optical mouse", "Wireless mouse", "Gaming mouse", "Mouse pad", "USB hub", "Webcam", "Document scanner"], 89.90m),
        ("audio", "AUD", ["Wired headset", "Wireless headset", "Studio headphones", "Bluetooth speaker", "Soundbar", "USB microphone", "Earbuds", "Audio interface", "Speaker stand", "Pop filter"], 149.90m),
        ("monitors", "MON", ["21 inch monitor", "24 inch monitor", "27 inch monitor", "32 inch monitor", "Ultrawide monitor", "Portable monitor", "Monitor arm", "Monitor stand", "Screen cleaner kit", "HDMI cable"], 799.00m),
        ("storage", "STO", ["120 GB SSD", "240 GB SSD", "500 GB SSD", "1 TB SSD", "2 TB SSD", "1 TB hard drive", "External SSD", "Memory card", "USB flash drive", "NAS enclosure"], 199.00m),
        ("accessories", "ACC", ["Laptop sleeve", "Laptop backpack", "Laptop stand", "Cable organizer", "Power strip", "Surge protector", "Phone holder", "Desk lamp", "Desk mat", "Cleaning wipes"], 39.90m),
    ];

    public static IReadOnlyList<Product> Create()
    {
        var brl = Currency.Create("BRL");
        var usd = Currency.Create("USD");
        var products = new List<Product>();

        foreach (var (category, prefix, names, basePrice) in _groups)
        {
            for (var index = 0; index < names.Length; index++)
            {
                var amount = decimal.Round(basePrice * (1 + (index * 0.35m)), 2, MidpointRounding.AwayFromZero);

                products.Add(Product.Create(
                    Sku.Create($"{prefix}-{index + 1:000}"),
                    names[index],
                    $"{names[index]} for the development catalog.",
                    category,
                    new Dictionary<string, string> { ["seed"] = "true" },
                    [Money.Create(amount, brl), Money.Create(decimal.Round(amount * _usdPerBrl, 2, MidpointRounding.AwayFromZero), usd)]));
            }
        }

        return products;
    }
}
