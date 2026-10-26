using System.Text.Json;
using System.Text.Json.Nodes;
using Orders.Application.Idempotency;

namespace Orders.Application.UnitTests;

public sealed class CanonicalRequestHashTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Compute_SamePropertiesInAnotherOrder_GivesTheSameHash()
    {
        var first = JsonNode.Parse("""{"currency":"BRL","items":[{"sku":"A","quantity":2},{"quantity":1,"sku":"B"}]}""");
        var second = JsonNode.Parse("""{"items":[{"quantity":2,"sku":"A"},{"sku":"B","quantity":1}],"currency":"BRL"}""");

        CanonicalRequestHash.Compute(first).ShouldBe(CanonicalRequestHash.Compute(second));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Compute_WhitespaceDiffers_GivesTheSameHash()
    {
        var compact = JsonNode.Parse("""{"a":1,"b":[1,2]}""");
        var spaced = JsonNode.Parse("{ \"a\" : 1,\n  \"b\" : [ 1, 2 ] }");

        CanonicalRequestHash.Compute(compact).ShouldBe(CanonicalRequestHash.Compute(spaced));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("""{"price":10}""", """{"price":10.00}""")]
    [InlineData("""{"price":10}""", """{"price":1e1}""")]
    [InlineData("""{"price":0.5}""", """{"price":0.50}""")]
    public void Compute_EquivalentDecimals_GiveTheSameHash(string first, string second)
    {
        CanonicalRequestHash.Compute(JsonNode.Parse(first)).ShouldBe(CanonicalRequestHash.Compute(JsonNode.Parse(second)));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("""{"quantity":1}""", """{"quantity":2}""")]
    [InlineData("""{"sku":"A"}""", """{"sku":"a"}""")]
    [InlineData("""{"items":[1,2]}""", """{"items":[2,1]}""")]
    [InlineData("""{"a":null}""", """{"a":false}""")]
    [InlineData("""{"a":"1"}""", """{"a":1}""")]
    [InlineData("""{"a":1}""", """{"a":1,"b":null}""")]
    public void Compute_DifferentContent_GivesADifferentHash(string first, string second)
    {
        CanonicalRequestHash.Compute(JsonNode.Parse(first)).ShouldNotBe(CanonicalRequestHash.Compute(JsonNode.Parse(second)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Compute_Result_IsASha256Digest()
    {
        CanonicalRequestHash.Compute(JsonNode.Parse("{}")).Length.ShouldBe(32);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Compute_ScalarsAndNull_AreSupported()
    {
        CanonicalRequestHash.Compute(null).ShouldNotBe(CanonicalRequestHash.Compute(JsonNode.Parse("0")));
        CanonicalRequestHash.Compute(JsonNode.Parse("true")).ShouldNotBe(CanonicalRequestHash.Compute(JsonNode.Parse("false")));
        CanonicalRequestHash.Compute(JsonNode.Parse("\"text\"")).Length.ShouldBe(32);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Compute_TypedRequest_HashesItsJsonForm()
    {
        var typed = new SampleRequest("BRL", [new SampleLine("A", 2.50m)]);

        CanonicalRequestHash.Compute(typed, new JsonSerializerOptions(JsonSerializerDefaults.Web)).ShouldBe(CanonicalRequestHash.Compute(
            JsonNode.Parse("""{"lines":[{"unitPrice":2.5,"sku":"A"}],"currency":"BRL"}""")));
    }

    private sealed record SampleRequest(string Currency, SampleLine[] Lines);

    private sealed record SampleLine(string Sku, decimal UnitPrice);
}
