using FluentValidation;
using Orders.Domain.Validation;

namespace Orders.Domain;

/// <summary>One priced line of an order. Name, price and catalog version are snapshots from Catalog.</summary>
public sealed record OrderItem
{
    private const int _maxSkuLength = 32;

    private static readonly InputValidator _validator = new();

    private OrderItem(string sku, string productName, Money unitPrice, int catalogVersion, int quantity, Money lineDiscount, Money lineTotal)
    {
        Sku = sku;
        ProductName = productName;
        UnitPrice = unitPrice;
        CatalogVersion = catalogVersion;
        Quantity = quantity;
        LineDiscount = lineDiscount;
        LineTotal = lineTotal;
    }

    public string Sku { get; }

    public string ProductName { get; }

    public Money UnitPrice { get; }

    public int CatalogVersion { get; }

    public int Quantity { get; }

    public Money LineDiscount { get; }

    /// <summary>Unit price times quantity, rounded once, minus the line discount.</summary>
    public Money LineTotal { get; }

    /// <summary>Unit price times quantity before any discount.</summary>
    public Money LineSubtotal => UnitPrice.Multiply(Quantity);

    public static OrderItem Create(
        string? sku, string? productName, Money unitPrice, int catalogVersion, int quantity, Money? lineDiscount = null)
    {
        var normalizedSku = sku?.Trim().ToUpperInvariant();
        var name = productName?.Trim();
        _validator.EnsureValid(new Input(normalizedSku, name, quantity));

        var discount = lineDiscount ?? Money.Zero(unitPrice.Currency);
        var subtotal = unitPrice.Multiply(quantity);

        if (discount.IsGreaterThan(subtotal))
        {
            throw new DomainException($"The discount on {normalizedSku} exceeds the line subtotal.");
        }

        return new OrderItem(normalizedSku!, name!, unitPrice, catalogVersion, quantity, discount, subtotal.Subtract(discount));
    }

    private sealed record Input(string? Sku, string? ProductName, int Quantity);

    private sealed class InputValidator : AbstractValidator<Input>
    {
        public InputValidator()
        {
            RuleFor(input => input.Sku)
                .Must(sku => !string.IsNullOrEmpty(sku) && sku.Length <= _maxSkuLength)
                .WithMessage($"SKU must have 1 to {_maxSkuLength} characters.");
            RuleFor(input => input.ProductName).NotEmpty().WithMessage("Product name must not be empty.");
            RuleFor(input => input.Quantity).GreaterThan(0).WithMessage("Quantity must be greater than zero.");
        }
    }
}
