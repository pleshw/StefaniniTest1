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

/// <summary>Orders.Api on PostgreSQL and RabbitMQ (Testcontainers): placing an order publishes OrderPlaced.v1 to orders.events.</summary>
public sealed class OrderPlacedRabbitMqTests : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private const string _pricesBody = """
        { "items": [ { "sku": "KB-1", "name": "Keyboard", "category": "peripherals", "unitPrice": 100.00, "currency": "BRL", "version": 3, "isActive": true } ], "missing": [] }
        """;

    private readonly PostgresFixture _postgres;
    private readonly RabbitMqFixture _rabbit;
    private readonly WireMockServer _catalog = WireMockServer.Start();
    private readonly string _queue = $"test.order-placed.{Guid.NewGuid():N}";
    private readonly Guid _shopper = Guid.NewGuid();
    private WebApplicationFactory<Program> _factory = null!;

    public OrderPlacedRabbitMqTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    {
        _postgres = postgres;
        _rabbit = rabbit;
    }

    public async ValueTask InitializeAsync()
    {
        _catalog.Given(Request.Create().WithPath("/api/v1/products/prices:query").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(_pricesBody));

        _factory = new RabbitApiFactory(_postgres.ConnectionString, _rabbit.AmqpUri, _catalog.Url!);
        _ = _factory.Services;
        await _rabbit.BindQueueAsync(_queue, "orders.events", "order.placed", Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _catalog.Dispose();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_PublishesOneOrderPlacedWithTheRoutingKeyAndTheTraceHeaders()
    {
        var client = _factory.CreateClient();

        var (status, body) = await Post(client, Guid.NewGuid().ToString());

        status.ShouldBe(HttpStatusCode.Created);
        var order = JsonDocument.Parse(body).RootElement;
        var messages = await _rabbit.WaitForMessagesAsync(_queue, 1, TimeSpan.FromSeconds(30), Token);
        await Task.Delay(500, Token);
        var extra = await _rabbit.GetMessagesAsync(_queue, 5, Token);
        messages.Count.ShouldBe(1);
        extra.ShouldBeEmpty();
        var message = messages[0];
        message.RoutingKey.ShouldBe("order.placed");
        message.Json.GetProperty("orderId").GetGuid().ShouldBe(order.GetProperty("id").GetGuid());
        message.Json.GetProperty("number").GetString().ShouldBe(order.GetProperty("number").GetString());
        message.Json.GetProperty("clientId").GetGuid().ShouldBe(_shopper);
        message.Json.GetProperty("channel").GetString().ShouldBe("shop");
        message.Json.GetProperty("currency").GetString().ShouldBe("BRL");
        message.Json.GetProperty("total").GetDecimal().ShouldBe(200m);
        message.Json.GetProperty("itemCount").GetInt32().ShouldBe(2);
        message.Header("traceparent").ShouldNotBeNull().ShouldMatch("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$");
        message.Header("occurred-at").ShouldNotBeNull();
        message.MessageId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_LogsOrderPlacedWithThePublishedPayloadInTheEventLog()
    {
        var client = _factory.CreateClient();

        var (_, body) = await Post(client, Guid.NewGuid().ToString());

        var orderId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
        var published = (await _rabbit.WaitForMessagesAsync(_queue, 1, TimeSpan.FromSeconds(30), Token)).ShouldHaveSingleItem();
        await using var context = _postgres.NewContext();
        var entry = await context.OrderEventLog.SingleAsync(log => log.OrderId == orderId, Token);
        entry.Type.ShouldBe("OrderPlaced.v1");
        JsonDocument.Parse(entry.Payload).RootElement.GetProperty("orderId").GetGuid().ShouldBe(orderId);
        JsonDocument.Parse(entry.Payload).RootElement.GetProperty("total").GetDecimal().ShouldBe(published.Json.GetProperty("total").GetDecimal());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_SameKeyReplayed_PublishesNothingMore()
    {
        var client = _factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        await Post(client, key);
        await _rabbit.WaitForMessagesAsync(_queue, 1, TimeSpan.FromSeconds(30), Token);

        await Post(client, key);

        await Task.Delay(1000, Token);
        (await _rabbit.GetMessagesAsync(_queue, 5, Token)).ShouldBeEmpty();
    }

    private async Task<(HttpStatusCode Status, string Body)> Post(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent("""{"currency":"BRL","items":[{"sku":"KB-1","quantity":2}]}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, _shopper.ToString());
        request.Headers.Add(TestAuthHandler.NameHeader, "Ana Souza");
        request.Headers.Add("Idempotency-Key", key);

        var response = await client.SendAsync(request, Token);

        return (response.StatusCode, await response.Content.ReadAsStringAsync(Token));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class RabbitApiFactory(string postgres, string rabbit, string catalogUrl) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Orders", postgres);
            builder.UseSetting("ConnectionStrings:RabbitMq", rabbit);
            builder.UseSetting("Orders:Persistence:AutoCreateMessageStorage", "true");
            builder.UseSetting("Catalog:BaseAddress", catalogUrl);
            builder.UseSetting("Catalog:Client:RetryDelay", "00:00:00.01");
            builder.ConfigureServices(services => services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
        }
    }
}
