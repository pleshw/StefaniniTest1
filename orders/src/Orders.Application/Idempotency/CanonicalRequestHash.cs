using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orders.Application.Idempotency;

/// <summary>
/// SHA-256 of a normalized JSON form of a request: properties sorted by name, no insignificant whitespace, numbers
/// written in invariant culture without trailing zeros. Two requests with the same content hash the same, whatever
/// the order of their properties.
/// </summary>
public static class CanonicalRequestHash
{
    public static byte[] Compute(JsonNode? body)
    {
        using var document = JsonDocument.Parse(body?.ToJsonString() ?? "null");

        return Hash(document.RootElement);
    }

    public static byte[] Compute<T>(T request, JsonSerializerOptions? options = null)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(request, options));

        return Hash(document.RootElement);
    }

    private static byte[] Hash(JsonElement element)
    {
        var canonical = new StringBuilder();
        Write(element, canonical);

        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
    }

    private static void Write(JsonElement element, StringBuilder output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(element, output);
                break;
            case JsonValueKind.Array:
                WriteArray(element, output);
                break;
            case JsonValueKind.String:
                output.Append(JsonSerializer.Serialize(element.GetString()));
                break;
            case JsonValueKind.Number:
                output.Append(NormalizeNumber(element.GetRawText()));
                break;
            case JsonValueKind.True:
                output.Append("true");
                break;
            case JsonValueKind.False:
                output.Append("false");
                break;
            default:
                output.Append("null");
                break;
        }
    }

    private static void WriteObject(JsonElement element, StringBuilder output)
    {
        output.Append('{');
        var first = true;

        foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            output.Append(first ? string.Empty : ",").Append(JsonSerializer.Serialize(property.Name)).Append(':');
            Write(property.Value, output);
            first = false;
        }

        output.Append('}');
    }

    private static void WriteArray(JsonElement element, StringBuilder output)
    {
        output.Append('[');
        var first = true;

        foreach (var item in element.EnumerateArray())
        {
            output.Append(first ? string.Empty : ",");
            Write(item, output);
            first = false;
        }

        output.Append(']');
    }

    private static string NormalizeNumber(string raw) =>
        decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("0.############################", CultureInfo.InvariantCulture)
            : raw;
}
