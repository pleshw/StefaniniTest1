using Catalog.Application.Products;
using FluentValidation.TestHelper;

namespace Catalog.Application.UnitTests;

public sealed class ValidatorTests
{
    private readonly UpsertProductCommandValidator _upsert = new(new PriceInputValidator(ProductTestData.Currencies));
    private readonly PriceQueryValidator _prices = new(ProductTestData.Currencies);
    private readonly ListProductsQueryValidator _list = new(ProductTestData.Currencies);
    private readonly GetProductQueryValidator _get = new(ProductTestData.Currencies);

    [Fact]
    [Trait("Category", "Unit")]
    public void UpsertProductCommand_ValidCommand_HasNoErrors()
    {
        _upsert.TestValidate(Upsert()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpsertProductCommand_BlankRequiredFields_ReportsEveryFieldAtOnce()
    {
        var result = _upsert.TestValidate(Upsert() with { Sku = " ", Name = " ", Category = null, Prices = null });

        result.ShouldHaveValidationErrorFor(command => command.Sku);
        result.ShouldHaveValidationErrorFor(command => command.Name).WithErrorMessage("Name must not be empty.");
        result.ShouldHaveValidationErrorFor(command => command.Category).WithErrorMessage("Category must not be empty.");
        result.ShouldHaveValidationErrorFor(command => command.Prices).WithErrorMessage("prices is required (it may be empty).");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpsertProductCommand_InvalidPrice_ReportsTheIndexedPrice()
    {
        var result = _upsert.TestValidate(Upsert() with { Prices = [new PriceInput("BRL", 1m), new PriceInput("EUR", null)] });

        result.ShouldHaveValidationErrorFor("Prices[1].Currency");
        result.ShouldHaveValidationErrorFor("Prices[1].Amount").WithErrorMessage("Every price needs an amount.");
        result.ShouldNotHaveValidationErrorFor("Prices[0].Currency");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpsertProductCommand_MalformedIfMatch_IsRejected()
    {
        _upsert.TestValidate(Upsert() with { IfMatch = "abc" }).ShouldHaveValidationErrorFor(command => command.IfMatch);
        _upsert.TestValidate(Upsert() with { IfMatch = "W/\"3\"" }).ShouldNotHaveValidationErrorFor(command => command.IfMatch);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PriceQuery_DuplicateSkus_IsRejected()
    {
        _prices.TestValidate(new PriceQuery("BRL", ["a", "A"]))
            .ShouldHaveValidationErrorFor(query => query.Skus).WithErrorMessage("skus must be distinct.");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ListProductsQuery_OptionalFieldsAbsent_HasNoErrors()
    {
        _list.TestValidate(new ListProductsQuery(null, null, null, null, null)).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(0)]
    [InlineData(ListProductsHandler.MaxLimit + 1)]
    public void ListProductsQuery_LimitOutOfRange_IsRejected(int limit)
    {
        _list.TestValidate(new ListProductsQuery(null, null, null, null, limit)).ShouldHaveValidationErrorFor(query => query.Limit);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetProductQuery_UnsupportedCurrency_IsRejected()
    {
        _get.TestValidate(new GetProductQuery("SKU-1", "EUR"))
            .ShouldHaveValidationErrorFor(query => query.Currency).WithErrorMessage("Currency 'EUR' is not supported.");
    }

    private static UpsertProductCommand Upsert() => new(
        "sku-1", null, "Keyboard", null, "peripherals", null, [new PriceInput("BRL", 100m)], "admin-1");
}
