using System.Globalization;

namespace Catalog.Domain.UnitTests;

public sealed class MoneyTests
{
    private static readonly Currency _brl = Currency.Create("BRL");

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("1.004", "1.00")]
    [InlineData("1.005", "1.01")]
    [InlineData("2.345", "2.35")]
    [InlineData("10", "10.00")]
    public void Create_MoreThanTwoDecimals_RoundsAwayFromZero(string amount, string expected)
    {
        Money.Create(Parse(amount), _brl).Amount.ShouldBe(Parse(expected));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_Zero_IsAccepted()
    {
        Money.Create(0m, _brl).Amount.ShouldBe(0m);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("-0.01")]
    [InlineData("-10")]
    public void Create_Negative_Throws(string amount)
    {
        Should.Throw<DomainException>(() => Money.Create(Parse(amount), _brl));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Equals_SameAmountAfterRoundingAndCurrency_AreEqual()
    {
        Money.Create(1.004m, _brl).ShouldBe(Money.Create(1.00m, _brl));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Equals_DifferentCurrency_AreNotEqual()
    {
        Money.Create(1m, _brl).ShouldNotBe(Money.Create(1m, Currency.Create("USD")));
    }

    private static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
