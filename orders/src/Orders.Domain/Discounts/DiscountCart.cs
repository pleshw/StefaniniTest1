using FluentValidation;
using Orders.Domain.Validation;

namespace Orders.Domain.Discounts;

/// <summary>One priced cart line, as discounts see it. The category is Catalog's, for category conditions.</summary>
public sealed record DiscountLine(string Sku, string Category, Money UnitPrice, int Quantity)
{
    public Money LineSubtotal => UnitPrice.Multiply(Quantity);
}

/// <summary>A priced cart in one currency, one line per SKU, that discount strategies and conditions read.</summary>
public sealed record DiscountCart
{
    private static readonly InputValidator _validator = new();

    private DiscountCart(Currency currency, IReadOnlyList<DiscountLine> lines)
    {
        Currency = currency;
        Lines = lines;
        Subtotal = lines.Aggregate(Money.Zero(currency), (sum, line) => sum.Add(line.LineSubtotal));
        ItemCount = lines.Sum(line => (long)line.Quantity);
    }

    public Currency Currency { get; }

    public IReadOnlyList<DiscountLine> Lines { get; }

    public Money Subtotal { get; }

    /// <summary>The sum of the quantities; a long because several lines may each hold up to int.MaxValue units.</summary>
    public long ItemCount { get; }

    public static DiscountCart Create(Currency currency, IEnumerable<DiscountLine> lines)
    {
        var list = lines.ToList();
        _validator.EnsureValid(new Input(currency, list));

        return new DiscountCart(currency, list);
    }

    private sealed record Input(Currency Currency, List<DiscountLine> Lines);

    private sealed class InputValidator : AbstractValidator<Input>
    {
        public InputValidator()
        {
            RuleForEach(input => input.Lines).ChildRules(line =>
            {
                line.RuleFor(item => item.Sku).NotEmpty().WithMessage("Every discount line needs a SKU.");
                line.RuleFor(item => item.Quantity).GreaterThan(0).WithMessage("Discount line quantities must be greater than zero.");
            });
            RuleFor(input => input.Lines)
                .Must((input, lines) => lines.TrueForAll(line => line.UnitPrice.Currency == input.Currency))
                .WithMessage("Every discount line must be priced in the cart currency.")
                .Must(lines => lines.Select(line => line.Sku).Distinct(StringComparer.Ordinal).Count() == lines.Count)
                .WithMessage("A discount cart has one line per SKU.");
        }
    }
}

/// <summary>The discount one line receives.</summary>
public sealed record DiscountLineAmount(string Sku, Money Amount);

/// <summary>
/// What one rule takes off a cart: either an order-level amount (no lines; the engine spreads it over the lines) or
/// one amount per discounted line.
/// </summary>
public sealed record DiscountResult
{
    private DiscountResult(Money total, IReadOnlyList<DiscountLineAmount> lines)
    {
        Total = total;
        Lines = lines;
    }

    public Money Total { get; }

    /// <summary>Per-line amounts of a line discount; empty for an order-level discount.</summary>
    public IReadOnlyList<DiscountLineAmount> Lines { get; }

    public bool IsZero => Total.Amount == 0m;

    public static DiscountResult None(Currency currency) => new(Money.Zero(currency), []);

    public static DiscountResult ForOrder(Money amount) => new(amount, []);

    /// <summary>Keeps only the lines with an amount, in the given order.</summary>
    public static DiscountResult ForLines(Currency currency, IEnumerable<DiscountLineAmount> lines)
    {
        var discounted = lines.Where(line => line.Amount.Amount > 0m).ToList();

        return new DiscountResult(discounted.Aggregate(Money.Zero(currency), (sum, line) => sum.Add(line.Amount)), discounted);
    }
}
