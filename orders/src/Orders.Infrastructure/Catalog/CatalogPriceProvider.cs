using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orders.Application;
using Orders.Application.Catalog;
using Orders.Application.Observability;
using Orders.Domain;
using Orders.Infrastructure.Catalog.Generated;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Orders.Infrastructure.Catalog;

internal sealed partial class CatalogPriceProvider(ICatalogApiClient client, OrdersMetrics metrics, ILogger<CatalogPriceProvider> logger) : ICatalogPriceProvider
{
    public async Task<Result<CatalogPrices>> GetPricesAsync(
        Currency currency, IReadOnlyCollection<string> skus, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var response = await client.QueryPricesAsync(
                new PriceQueryRequest { Currency = currency.Code, Skus = skus.ToList() }, cancellationToken);

            var found = response.Items
                .Select(item => new CatalogPrice(
                    item.Sku, item.Name, item.Category, Money.Create(item.UnitPrice, Currency.Create(item.Currency)), item.Version, item.IsActive))
                .ToList();

            metrics.PriceLookup(Stopwatch.GetElapsedTime(started), succeeded: true);

            return Result.Ok(new CatalogPrices(found, response.Missing.ToList()));
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException or BrokenCircuitException or TimeoutRejectedException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            metrics.PriceLookup(Stopwatch.GetElapsedTime(started), succeeded: false);
            LogCatalogFailed(logger, exception.GetType().Name, exception);

            return Result.Fail<CatalogPrices>(ErrorKind.CatalogUnavailable, "The catalog could not be reached; try again shortly.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Catalog price lookup failed ({FailureKind}).")]
    private static partial void LogCatalogFailed(ILogger logger, string failureKind, Exception exception);
}
