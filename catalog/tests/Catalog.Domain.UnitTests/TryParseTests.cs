namespace Catalog.Domain.UnitTests;

public sealed class TryParseTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void SkuTryCreate_ValidValue_ReturnsNormalizedSku()
    {
        Sku.TryCreate(" ab ", out var sku).ShouldBeTrue();

        sku!.Value.ShouldBe("AB");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData(" ")]
    public void SkuTryCreate_BlankValue_ReturnsFalse(string? value)
    {
        Sku.TryCreate(value, out var sku).ShouldBeFalse();

        sku.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CurrencyPolicyTryParse_AllowedCode_ReturnsCurrency()
    {
        new CurrencyPolicy(["BRL"]).TryParse("brl", out var currency).ShouldBeTrue();

        currency!.Code.ShouldBe("BRL");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("USD")]
    [InlineData("US")]
    [InlineData(null)]
    public void CurrencyPolicyTryParse_NotAllowedOrMalformed_ReturnsFalse(string? code)
    {
        new CurrencyPolicy(["BRL"]).TryParse(code, out var currency).ShouldBeFalse();

        currency.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CurrencyPolicyDefault_SeveralAllowed_IsTheFirstConfigured()
    {
        new CurrencyPolicy(["usd", "BRL"]).Default.Code.ShouldBe("USD");
    }
}
