using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Orders.Application.Catalog;
using Orders.Application.Observability;
using Orders.Infrastructure.Catalog.Generated;
using Polly;

namespace Orders.Infrastructure.Catalog;

public sealed record CatalogClientOptions
{
    public const string SectionName = "Catalog";

    public const string DefaultBaseAddress = "http://catalog-api:8080";

    public string BaseAddress { get; init; } = DefaultBaseAddress;

    public CatalogResilienceOptions Client { get; init; } = new();
}

public sealed record CatalogResilienceOptions
{
    /// <summary>Time allowed for one attempt.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Retries after the first attempt, for transient errors (5xx, 408, 429, network failures, timeouts).</summary>
    public int MaxRetryAttempts { get; init; } = 2;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    public CatalogCircuitBreakerOptions CircuitBreaker { get; init; } = new();
}

public sealed record CatalogCircuitBreakerOptions
{
    public double FailureRatio { get; init; } = 0.5;

    public int MinimumThroughput { get; init; } = 10;

    public TimeSpan SamplingDuration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(15);
}

public static class CatalogClientExtensions
{
    private const string _clientName = "catalog";

    /// <summary>
    /// Registers <see cref="ICatalogPriceProvider"/> on a typed HttpClient generated from Catalog's OpenAPI document.
    /// The host's service defaults add a default resilience handler to every client; the catalog client replaces it
    /// with the standard handler configured here (timeout, retries, circuit breaker), so retries never multiply.
    /// </summary>
    public static IServiceCollection AddCatalogClient(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(CatalogClientOptions.SectionName).Get<CatalogClientOptions>() ?? new CatalogClientOptions();
        var resilience = options.Client;

        var client = services.AddHttpClient<ICatalogApiClient, CatalogApiClient>(_clientName, http => http.BaseAddress = new Uri(options.BaseAddress));
#pragma warning disable EXTEXP0001 // Experimental, but the supported way to replace the handler added by ConfigureHttpClientDefaults.
        client.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        client.AddStandardResilienceHandler(pipeline =>
            {
                pipeline.AttemptTimeout.Timeout = resilience.AttemptTimeout;
                pipeline.Retry.MaxRetryAttempts = resilience.MaxRetryAttempts;
                pipeline.Retry.BackoffType = DelayBackoffType.Constant;
                pipeline.Retry.UseJitter = false;
                pipeline.Retry.Delay = resilience.RetryDelay;
                pipeline.CircuitBreaker.FailureRatio = resilience.CircuitBreaker.FailureRatio;
                pipeline.CircuitBreaker.MinimumThroughput = resilience.CircuitBreaker.MinimumThroughput;
                pipeline.CircuitBreaker.SamplingDuration = resilience.CircuitBreaker.SamplingDuration;
                pipeline.CircuitBreaker.BreakDuration = resilience.CircuitBreaker.BreakDuration;
                pipeline.TotalRequestTimeout.Timeout = (resilience.AttemptTimeout + resilience.RetryDelay) * (resilience.MaxRetryAttempts + 1);
            });
        services.TryAddSingleton<OrdersMetrics>();
        services.AddScoped<ICatalogPriceProvider, CatalogPriceProvider>();

        return services;
    }
}
