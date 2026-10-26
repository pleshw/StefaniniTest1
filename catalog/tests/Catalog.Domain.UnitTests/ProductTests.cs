namespace Catalog.Domain.UnitTests;

public sealed class ProductTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Create_ValidData_StartsActiveAtVersionOneWithoutEvents()
    {
        var product = new ProductBuilder().Build();

        product.IsActive.ShouldBeTrue();
        product.Version.ShouldBe(1);
        product.DomainEvents.ShouldBeEmpty();
        product.Prices.Count.ShouldBe(1);
        product.Attributes["layout"].ShouldBe("abnt2");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_NoPrices_IsAccepted()
    {
        new ProductBuilder().WithPrices().Build().Prices.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_NullDescriptionAndAttributes_UseEmptyValues()
    {
        var product = Product.Create(Sku.Create("S"), "Name", null, "cat", null, []);

        product.Description.ShouldBe(string.Empty);
        product.Attributes.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_DuplicateCurrency_Throws()
    {
        var builder = new ProductBuilder().WithPrices(ProductBuilder.Brl(10m), ProductBuilder.Brl(20m));

        Should.Throw<DomainException>(builder.Build);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("")]
    [InlineData("  ")]
    public void Create_BlankName_Throws(string name)
    {
        var builder = new ProductBuilder().WithName(name);

        Should.Throw<DomainException>(builder.Build);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("")]
    [InlineData("  ")]
    public void Create_BlankCategory_Throws(string category)
    {
        var builder = new ProductBuilder().WithCategory(category);

        Should.Throw<DomainException>(builder.Build);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ChangePrice_ExistingCurrency_ReplacesPriceBumpsVersionAndRaisesEvent()
    {
        var product = new ProductBuilder().Build();

        product.ChangePrice(ProductBuilder.Brl(120m));

        product.Prices.Single().Amount.ShouldBe(120m);
        product.Version.ShouldBe(2);
        var raised = product.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<PriceChanged>();
        raised.Sku.ShouldBe(product.Sku);
        raised.OldPrice.ShouldBe(ProductBuilder.Brl(100m));
        raised.NewPrice.ShouldBe(ProductBuilder.Brl(120m));
        raised.Version.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ChangePrice_CurrencyWithoutPrice_AddsPriceWithNoOldValue()
    {
        var product = new ProductBuilder().Build();

        product.ChangePrice(ProductBuilder.Usd(20m));

        product.Prices.Count.ShouldBe(2);
        product.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<PriceChanged>().OldPrice.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ChangePrice_SamePrice_ChangesNothing()
    {
        var product = new ProductBuilder().Build();

        product.ChangePrice(ProductBuilder.Brl(100m));

        product.Version.ShouldBe(1);
        product.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ChangePrice_DeactivatedProduct_Throws()
    {
        var product = new ProductBuilder().Build();
        product.Deactivate();

        Should.Throw<DomainException>(() => product.ChangePrice(ProductBuilder.Brl(1m)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Rename_NewName_BumpsVersion()
    {
        var product = new ProductBuilder().Build();

        product.Rename("  Mechanical keyboard ");

        product.Name.ShouldBe("Mechanical keyboard");
        product.Version.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Rename_SameName_ChangesNothing()
    {
        var product = new ProductBuilder().Build();

        product.Rename("Keyboard");

        product.Version.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Rename_BlankName_Throws()
    {
        var product = new ProductBuilder().Build();

        Should.Throw<DomainException>(() => product.Rename(" "));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Deactivate_ActiveProduct_DeactivatesAndBumpsVersion()
    {
        var product = new ProductBuilder().Build();

        product.Deactivate();

        product.IsActive.ShouldBeFalse();
        product.Version.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Deactivate_AlreadyInactive_ChangesNothing()
    {
        var product = new ProductBuilder().Build();
        product.Deactivate();

        product.Deactivate();

        product.Version.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClearDomainEvents_AfterPriceChange_EmptiesEvents()
    {
        var product = new ProductBuilder().Build();
        product.ChangePrice(ProductBuilder.Brl(1m));

        product.ClearDomainEvents();

        product.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Restore_StoredState_KeepsVersionAndActiveFlagWithoutEvents()
    {
        var product = Product.Restore(
            Sku.Create("S"), "Name", "desc", "cat", new Dictionary<string, string>(), [ProductBuilder.Brl(5m)], false, 7);

        product.Version.ShouldBe(7);
        product.IsActive.ShouldBeFalse();
        product.DomainEvents.ShouldBeEmpty();
        product.Prices.Single().Amount.ShouldBe(5m);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Restore_VersionBelowOne_Throws()
    {
        Should.Throw<DomainException>(() => Product.Restore(
            Sku.Create("S"), "Name", "desc", "cat", new Dictionary<string, string>(), [], true, 0));
    }
}
