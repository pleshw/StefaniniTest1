using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orders.Application;
using Orders.Application.Catalog;
using Orders.Domain;
using Orders.Infrastructure.Catalog;
using ServiceDefaults;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace Orders.IntegrationTests;

/// <summary>The Catalog client against a WireMock server standing in for Catalog.Api (no Docker needed).</summary>
public sealed class CatalogPriceProviderTests : IDisposable
{
    private const string _pricesPath = "/api/v1/products/prices:query";

    private const string _twoItemsBody = """
        {
          "items": [
            { "sku": "KB-1", "name": "Keyboard", "category": "peripherals", "unitPrice": 19.99, "currency": "BRL", "version": 3, "isActive": true },
            { "sku": "MS-1", "name": "Mouse", "category": "peripherals", "unitPrice": 10, "currency": "BRL", "version": 1, "isActive": false }
          ],
          "missing": ["GONE-1"]
        }
        """;

    private readonly WireMockServer _catalog = WireMockServer.Start();

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetPricesAsync_CatalogAnswers_MapsPricesAndMissingSkus()
    {
        Respond(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(_twoItemsBody));
        await using var provider = BuildProvider();

        var result = await Provider(provider).GetPricesAsync(Brl, ["KB-1", "MS-1", "GONE-1"], Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Missing.ShouldBe(["GONE-1"]);
        result.Value.Found.Select(price => price.Sku).ShouldBe(["KB-1", "MS-1"]);
        result.Value.Found[0].ShouldBe(new CatalogPrice("KB-1", "Keyboard", "peripherals", Money.Create(19.99m, Brl), 3, true));
        result.Value.Found[1].IsActive.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetPricesAsync_PriceBeyondDoublePrecision_KeepsEveryCent()
    {
        const string body = """
            { "items": [ { "sku": "BIG-1", "name": "Vault", "category": "safes", "unitPrice": 10000000000000.01, "currency": "BRL", "version": 1, "isActive": true } ], "missing": [] }
            """;
        Respond(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body));
        await using var provider = BuildProvider();

        var result = await Provider(provider).GetPricesAsync(Brl, ["BIG-1"], Token);

        result.Value.Found.ShouldHaveSingleItem().UnitPrice.Amount.ShouldBe(10000000000000.01m);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetPricesAsync_Always_SendsOneRequestWithTheCurrencyAndAllSkus()
    {
        Respond(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(_twoItemsBody));
        await using var provider = BuildProvider();

        await Provider(provider).GetPricesAsync(Brl, ["KB-1", "MS-1"], Token);

        var request = _catalog.LogEntries.ShouldHaveSingleItem().RequestMessage.ShouldNotBeNull();
        request.Body.ShouldNotBeNull();
        request.Body.ShouldContain("\"currency\":\"BRL\"");
        request.Body.ShouldContain("KB-1");
        request.Body.ShouldContain("MS-1");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetPricesAsync_ServerErrorThenSuccess_RetriesAndSucceeds()
    {
        var calls = 0;
        Respond(Response.Create().WithCallback(_ => Interlocked.Increment(ref calls) == 1
            ? new ResponseMessage { StatusCode = 503 }
            : new ResponseMessage
            {
                StatusCode = 200,
                Headers = new Dictionary<string, WireMockList<string>> { ["Content-Type"] = new("application/json") },
                BodyData = new BodyData { DetectedBodyType = BodyType.String, BodyAsString = _twoItemsBody },
            }));
        await using var provider = BuildProvider();

        var result = await Provider(provider).GetPricesAsync(Brl, ["KB-1"], Token);

        result.IsSuccess.ShouldBeTrue();
        _catalog.LogEntries.Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetPricesAsync_ServerErrorEveryTime_GivesUpAfterTwoRetriesWithCatalogUnavailable()
    {
        Respond(Response.Create().WithStatusCode(500));
        await using var provider = BuildProvider();

        var result = await Provider(provider).GetPricesAsync(Brl, ["KB-1"], Token);

        result.Error!.Kind.ShouldBe(ErrorKind.CatalogUnavailable);
        _catalog.LogEntries.Count.ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetPricesAsync_CatalogTooSlow_ReturnsCatalogUnavailable()
    {
        Respond(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(2)).WithBody(_twoItemsBody));
        await using var provider = BuildProvider(("Catalog:Client:AttemptTimeout", "00:00:00.2"), ("Catalog:Client:MaxRetryAttempts", "1"));

        var result = await Provider(provider).GetPricesAsync(Brl, ["KB-1"], Token);

        result.Error!.Kind.ShouldBe(ErrorKind.CatalogUnavailable);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetPricesAsync_CatalogUnreachable_ReturnsCatalogUnavailable()
    {
        _catalog.Stop();
        await using var provider = BuildProvider(("Catalog:Client:MaxRetryAttempts", "1"));

        var result = await Provider(provider).GetPricesAsync(Brl, ["KB-1"], Token);

        result.Error!.Kind.ShouldBe(ErrorKind.CatalogUnavailable);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetPricesAsync_UnexpectedClientError_ReturnsCatalogUnavailable()
    {
        Respond(Response.Create().WithStatusCode(400));
        await using var provider = BuildProvider();

        var result = await Provider(provider).GetPricesAsync(Brl, ["KB-1"], Token);

        result.Error!.Kind.ShouldBe(ErrorKind.CatalogUnavailable);
        _catalog.LogEntries.Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetPricesAsync_CircuitOpens_FailsFastWithoutCallingCatalog()
    {
        Respond(Response.Create().WithStatusCode(500));
        await using var provider = BuildProvider(
            ("Catalog:Client:MaxRetryAttempts", "1"),
            ("Catalog:Client:CircuitBreaker:MinimumThroughput", "2"),
            ("Catalog:Client:CircuitBreaker:BreakDuration", "00:01:00"));
        var catalogClient = Provider(provider);
        await catalogClient.GetPricesAsync(Brl, ["KB-1"], Token);
        var callsBeforeOpen = _catalog.LogEntries.Count;

        var result = await catalogClient.GetPricesAsync(Brl, ["KB-1"], Token);

        result.Error!.Kind.ShouldBe(ErrorKind.CatalogUnavailable);
        callsBeforeOpen.ShouldBe(2);
        _catalog.LogEntries.Count.ShouldBe(2);
    }

    public void Dispose() => _catalog.Dispose();

    private static Currency Brl => Currency.Create("BRL");

    private void Respond(IResponseBuilder response) =>
        _catalog.Given(Request.Create().WithPath(_pricesPath).UsingPost()).RespondWith(response);

    private ServiceProvider BuildProvider(params (string Key, string Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Catalog:BaseAddress"] = _catalog.Url,
            ["Catalog:Client:RetryDelay"] = "00:00:00.01",
        };

        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddServiceDefaults();
        builder.Services.AddCatalogClient(builder.Configuration);

        return builder.Services.BuildServiceProvider();
    }

    private static ICatalogPriceProvider Provider(ServiceProvider provider) =>
        provider.GetRequiredService<ICatalogPriceProvider>();

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
