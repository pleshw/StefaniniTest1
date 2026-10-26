using System.Globalization;

namespace Orders.Application;

/// <summary>The ETag and If-Match form of a version number: <c>"3"</c>, or <c>W/"3"</c> when read.</summary>
public static class EntityTag
{
    public static string For(int version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    public static bool TryParseVersion(string etag, out int version)
    {
        var value = etag.Trim();
        value = value.StartsWith("W/", StringComparison.Ordinal) ? value[2..] : value;
        version = 0;

        return value.Length > 2 && value[0] == '"' && value[^1] == '"'
            && int.TryParse(value[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }
}
