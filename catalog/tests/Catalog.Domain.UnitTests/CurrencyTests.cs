namespace Catalog.Domain.UnitTests;

public sealed class CurrencyTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("BRL", "BRL")]
    [InlineData("usd", "USD")]
    [InlineData(" eur ", "EUR")]
    public void Create_ThreeLetterCode_NormalizesToUpperCase(string code, string expected)
    {
        Currency.Create(code).Code.ShouldBe(expected);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("U5D")]
    public void Create_NotThreeLetters_Throws(string? code)
    {
        Should.Throw<DomainException>(() => Currency.Create(code));
    }
}

public sealed class CurrencyPolicyTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_AllowedCode_ReturnsCurrencyIgnoringCase()
    {
        var policy = new CurrencyPolicy(["BRL", "USD"]);

        policy.Parse("usd").Code.ShouldBe("USD");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_CodeOutsideAllowList_Throws()
    {
        var policy = new CurrencyPolicy(["BRL"]);

        Should.Throw<DomainException>(() => policy.Parse("USD"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_MalformedCode_Throws()
    {
        var policy = new CurrencyPolicy(["BRL"]);

        Should.Throw<DomainException>(() => policy.Parse("BR"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Constructor_EmptyAllowList_Throws()
    {
        Should.Throw<DomainException>(() => new CurrencyPolicy([]));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Constructor_MalformedCodeInAllowList_Throws()
    {
        Should.Throw<DomainException>(() => new CurrencyPolicy(["BRL", "X"]));
    }
}
