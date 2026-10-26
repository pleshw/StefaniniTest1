using Catalog.Application.Products;

namespace Catalog.Application.UnitTests;

public sealed class ProductCursorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void TryDecode_EncodedCursor_RoundTripsNameAndSku()
    {
        var encoded = new ProductCursor("Teclado \"gamer\" ção", "SKU-1").Encode();

        ProductCursor.TryDecode(encoded, out var cursor).ShouldBeTrue();

        cursor.ShouldBe(new ProductCursor("Teclado \"gamer\" ção", "SKU-1"));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("")]
    [InlineData("%%%")]
    [InlineData("e30")]
    [InlineData("WyJhIl0")]
    public void TryDecode_MalformedValue_ReturnsFalse(string value)
    {
        ProductCursor.TryDecode(value, out var cursor).ShouldBeFalse();

        cursor.ShouldBeNull();
    }
}
