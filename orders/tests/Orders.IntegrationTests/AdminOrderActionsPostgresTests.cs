using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orders.Domain;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Orders.IntegrationTests;

/// <summary>Admin order actions end to end: real Orders.Api, PostgreSQL and RabbitMQ (Testcontainers), a WireMock Catalog.</summary>
public sealed class AdminOrderActionsPostgresTests : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private const string _pricesBody = """
        { "items": [ { "sku": "KB-1", "name": "Keyboard", "category": "peripherals", "unitPrice": 100.00, "currency": "BRL", "version": 3, "isActive": true } ], "missing": [] }
        """;

    private readonly PostgresFixture _postgres;
    private readonly RabbitMqFixture _rabbit;
    private readonly WireMockServer _catalog = WireMockServer.Start();
    private readonly string _spyQueue = $"test.admin-status.{Guid.NewGuid():N}";
    private readonly string _shopper = Guid.NewGuid().ToString();
    private WebApplicationFactory<Program> _factory = null!;

    public AdminOrderActionsPostgresTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    {
        _postgres = postgres;
        _rabbit = rabbit;
    }

    public async ValueTask InitializeAsync()
    {
        _catalog.Given(Request.Create().WithPath("/api/v1/products/prices:query").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(_pricesBody));
        _factory = new ApiFactory(_postgres.ConnectionString, _rabbit.AmqpUri, _catalog.Url!);
        _ = _factory.Services;
        await _rabbit.BindQueueAsync(_spyQueue, "orders.events", "order.status-changed", Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _catalog.Dispose();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Hold_PlacedOrder_WritesHistoryEventLogAndPublishesOrderStatusChanged()
    {
        var client = _factory.CreateClient();
        var id = await PlaceOrder(client);

        var response = await Act(client, id, "hold", "\"0\"", "Call the customer");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
        await using var context = _postgres.NewContext();
        var order = await context.Orders.Include(record => record.History).SingleAsync(record => record.Id == id, Token);
        order.Status.ShouldBe(OrderStatus.OnHold);
        order.RowVersion.ShouldBe(1);
        var change = order.History.OrderBy(history => history.Seq).Last();
        (change.FromStatus, change.ToStatus, change.Reason, change.Actor).ShouldBe((OrderStatus.Pending, OrderStatus.OnHold, "Call the customer", "admin-1"));
        var log = await context.OrderEventLog.SingleAsync(entry => entry.OrderId == id, Token);
        log.Type.ShouldBe("OrderStatusChanged.v1");
        var published = await _rabbit.WaitForMessagesAsync(_spyQueue, 1, TimeSpan.FromSeconds(30), Token);
        published.Count.ShouldBe(1);
        published[0].RoutingKey.ShouldBe("order.status-changed");
        published[0].Json.GetProperty("to").GetString().ShouldBe("OnHold");
        published[0].Json.GetProperty("actor").GetString().ShouldBe("admin-1");
        JsonDocument.Parse(log.Payload).RootElement.GetProperty("reason").GetString().ShouldBe("Call the customer");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Confirm_TwoAdminsWithTheSameIfMatch_OneWinsAndTheOtherGets412()
    {
        var client = _factory.CreateClient();
        var id = await PlaceOrder(client);

        var responses = await Task.WhenAll(Act(client, id, "confirm", "\"0\"", null), Act(client, id, "block", "\"0\"", "fraud"));

        responses.Select(response => response.StatusCode).Order().ShouldBe([HttpStatusCode.OK, HttpStatusCode.PreconditionFailed]);
        await using var context = _postgres.NewContext();
        (await context.Orders.Where(record => record.Id == id).Select(record => record.RowVersion).SingleAsync(Token)).ShouldBe(1);
        (await context.OrderEventLog.CountAsync(entry => entry.OrderId == id, Token)).ShouldBe(1);
        (await _rabbit.WaitForMessagesAsync(_spyQueue, 1, TimeSpan.FromSeconds(30), Token)).Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Cancel_PendingOrder_Returns409AndWritesNothing()
    {
        var client = _factory.CreateClient();
        var id = await PlaceOrder(client);

        var response = await Act(client, id, "cancel", "\"0\"", "customer asked");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("type").GetString().ShouldBe("invalid-transition");
        await using var context = _postgres.NewContext();
        (await context.OrderEventLog.CountAsync(entry => entry.OrderId == id, Token)).ShouldBe(0);
        (await context.Orders.Where(record => record.Id == id).Select(record => record.Status).SingleAsync(Token)).ShouldBe(OrderStatus.Pending);
    }

    private async Task<Guid> PlaceOrder(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent("""{"currency":"BRL","items":[{"sku":"KB-1","quantity":1}]}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, _shopper);
        request.Headers.Add(TestAuthHandler.NameHeader, "Ana Souza");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Token);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> Act(HttpClient client, Guid id, string action, string ifMatch, string? reason)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/orders/{id}/{action}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { reason }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, "admin-1");
        request.Headers.Add(TestAuthHandler.RolesHeader, "Admin");
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        return await client.SendAsync(request, Token);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class ApiFactory(string postgres, string rabbit, string catalogUrl) : WebApplicationFactory<Program>
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
