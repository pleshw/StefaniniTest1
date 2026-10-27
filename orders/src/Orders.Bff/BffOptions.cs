namespace Orders.Bff;

/// <summary>Where the BFF finds Orders and Catalog, and what it accepts from the browser.</summary>
public sealed record BffOptions
{
    public const string SectionName = "Bff";

    public ServiceAddress Orders { get; init; } = new() { BaseAddress = "http://orders-api:8080" };

    public ServiceAddress Catalog { get; init; } = new() { BaseAddress = "http://catalog-api:8080" };

    /// <summary>Largest request body the BFF reads or forwards, in bytes (1 MiB by default).</summary>
    public long MaxRequestBodySize { get; init; } = 1024 * 1024;
}

public sealed record ServiceAddress
{
    public required string BaseAddress { get; init; }
}
