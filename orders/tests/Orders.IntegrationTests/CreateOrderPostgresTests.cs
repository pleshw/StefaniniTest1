using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Orders.IntegrationTests;

/// <summary>Create order end to end: real Orders.Api, PostgreSQL (Testcontainers), Wolverine's outbox and a WireMock Catalog.</summary>
public sealed class CreateOrderPostgresTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string _shopper = "11111111-1111-1111-1111-111111111111";

    private const string _pricesBody = """
        { "items": [ { "sku": "KB-1", "name": "Keyboard", "category": "peripherals", "unitPrice": 19.99, "currency": "BRL", "version": 3, "isActive": true } ], "missing": [] }
        """;

    private readonly PostgresFixture _postgres;
    private readonly WireMockServer _catalog = WireMockServer.Start();
    private PostgresApiFactory _factory = null!;

    public CreateOrderPostgresTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    public ValueTask InitializeAsync()
    {
        _catalog.Given(Request.Create().WithPath("/api/v1/products/prices:query").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(_pricesBody));
        _factory = new PostgresApiFactory(_postgres.ConnectionString, _catalog.Url!);
        _ = _factory.Services;

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _catalog.Dispose();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_TenIdenticalRequestsInParallel_CreateExactlyOneOrderAndReturnTenIdenticalResponses()
    {
        var key = Guid.NewGuid().ToString();
        var client = _factory.CreateClient();
        var ordersBefore = await CountOrders();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Post(client, key, quantity: 2)));

        responses.ShouldAllBe(response => response.Status == HttpStatusCode.Created);
        responses.Select(response => response.Body).Distinct().Count().ShouldBe(1);
        (await CountOrders()).ShouldBe(ordersBefore + 1);
        await using var context = _postgres.NewContext();
        (await context.IdempotencyRequests.CountAsync(request => request.IdempotencyKey == Guid.Parse(key), Token)).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_SameKeyWithAnotherBody_Returns422AndCreatesNoSecondOrder()
    {
        var key = Guid.NewGuid().ToString();
        var client = _factory.CreateClient();
        await Post(client, key, quantity: 1);
        var ordersAfterFirst = await CountOrders();

        var second = await Post(client, key, quantity: 5);

        second.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CountOrders()).ShouldBe(ordersAfterFirst);
    }

    private async Task<int> CountOrders()
    {
        await using var context = _postgres.NewContext();

        return await context.Orders.CountAsync(Token);
    }

    private static async Task<(HttpStatusCode Status, string Body)> Post(HttpClient client, string key, int quantity)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { currency = "BRL", items = new[] { new { sku = "KB-1", quantity } } }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, _shopper);
        request.Headers.Add(TestAuthHandler.NameHeader, "Ana Souza");
        request.Headers.Add("Idempotency-Key", key);

        var response = await client.SendAsync(request, Token);

        return (response.StatusCode, await response.Content.ReadAsStringAsync(Token));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class PostgresApiFactory(string connectionString, string catalogUrl) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Orders", connectionString);
            builder.UseSetting("Orders:Persistence:AutoCreateMessageStorage", "true");
            builder.UseSetting("Catalog:BaseAddress", catalogUrl);
            builder.UseSetting("Catalog:Client:RetryDelay", "00:00:00.01");
            builder.ConfigureServices(services => services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
        }
    }
}
