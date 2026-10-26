namespace Catalog.Domain.UnitTests;

public sealed class SkuTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("ABC-1", "ABC-1")]
    [InlineData("abc-1", "ABC-1")]
    [InlineData("  abc-1  ", "ABC-1")]
    public void Create_ValidValue_NormalizesToTrimmedUpperCase(string value, string expected)
    {
        Sku.Create(value).Value.ShouldBe(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_ExactlyMaxLength_IsAccepted()
    {
        Sku.Create(new string('a', Sku.MaxLength)).Value.Length.ShouldBe(32);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_NullOrBlank_Throws(string? value)
    {
        Should.Throw<DomainException>(() => Sku.Create(value));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_LongerThanMaxLength_Throws()
    {
        Should.Throw<DomainException>(() => Sku.Create(new string('A', Sku.MaxLength + 1)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Equals_SameValueDifferentCase_AreEqual()
    {
        Sku.Create("abc").ShouldBe(Sku.Create("ABC"));
    }
}
