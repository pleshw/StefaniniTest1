extern alias OrdersBff;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Orders.IntegrationTests;

/// <summary>
/// The live Admin feed end to end: the real BFF (its own RabbitMQ queue, the SSE endpoint) in front of the real
/// Orders.Api on PostgreSQL and RabbitMQ (Testcontainers), with service tokens and the development sign-in. An order
/// placed through the Shop routes reaches an open Admin stream; a stream that reconnects gets what it missed.
/// </summary>
public sealed partial class LiveEventStreamRabbitMqTests : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private const string _signingKey = "live-stream-tests-service-token-signing-key-0123456789";
    private const string _pricesBody = """
        { "items": [ { "sku": "KB-1", "name": "Keyboard", "category": "peripherals", "unitPrice": 100.00, "currency": "BRL", "version": 3, "isActive": true } ], "missing": [] }
        """;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    private readonly PostgresFixture _postgres;
    private readonly RabbitMqFixture _rabbit;
    private readonly WireMockServer _catalog = WireMockServer.Start();
    private WebApplicationFactory<Program> _orders = null!;
    private WebApplicationFactory<OrdersBff::Program> _bff = null!;

    public LiveEventStreamRabbitMqTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    {
        _postgres = postgres;
        _rabbit = rabbit;
    }

    public async ValueTask InitializeAsync()
    {
        _catalog.Given(Request.Create().WithPath("/api/v1/products/prices:query").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(_pricesBody));
        _orders = new OrdersFactory(_postgres.ConnectionString, _rabbit.AmqpUri, _catalog.Url!);
        _ = _orders.Services;
        _bff = new BffFactory(_rabbit.AmqpUri, _orders);
        _ = _bff.Services;
        await _rabbit.WaitForBindingAsync("orders.events", "#", _timeout, Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _bff.DisposeAsync();
        await _orders.DisposeAsync();
        _catalog.Dispose();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OrderPlacedInTheShop_ReachesTheOpenAdminStream_AndAReconnectGetsWhatItMissed()
    {
        var admin = await SignIn("admin", "admin-password");
        var shopper = await SignIn("shopper", "shopper-password");

        string cursor;
        Guid first;
        using (var stream = await OpenStream(admin, lastEventId: null))
        {
            first = await PlaceOrder(shopper);
            var placed = await stream.NextOfTypeAsync("OrderPlaced.v1", _timeout, Token);
            JsonDocument.Parse(placed.Data).RootElement.GetProperty("orderId").GetGuid().ShouldBe(first);
            placed.Id.ShouldNotBeNullOrEmpty();
            cursor = placed.Id!;
        }

        var missed = await PlaceOrder(shopper);

        using var reconnected = await OpenStream(admin, cursor);
        var backfilled = await reconnected.NextOfTypeAsync("OrderPlaced.v1", _timeout, Token);
        JsonDocument.Parse(backfilled.Data).RootElement.GetProperty("orderId").GetGuid().ShouldBe(missed);
        missed.ShouldNotBe(first);
    }

    private async Task<HttpClient> SignIn(string user, string password)
    {
        var browser = _bff.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var form = await browser.GetStringAsync("/bff/login", Token);
        var token = AntiforgeryField().Match(form);
        var response = await browser.PostAsync(
            "/bff/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [token.Groups["name"].Value] = token.Groups["value"].Value,
                ["username"] = user,
                ["password"] = password,
            }),
            Token);
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        browser.DefaultRequestHeaders.Add("X-CSRF", "1");

        return browser;
    }

    private static async Task<Guid> PlaceOrder(HttpClient shopper)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/shop/orders")
        {
            Content = new StringContent("""{"currency":"BRL","items":[{"sku":"KB-1","quantity":1}]}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await shopper.SendAsync(request, Token);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
    }

    private static async Task<EventStreamReader> OpenStream(HttpClient admin, string? lastEventId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/bff/admin/events/stream");

        if (lastEventId is not null)
        {
            request.Headers.Add("Last-Event-ID", lastEventId);
        }

        var response = await admin.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return new EventStreamReader(response, await response.Content.ReadAsStreamAsync(Token));
    }

    [GeneratedRegex("name=\"(?<name>[^\"]+)\" type=\"hidden\" value=\"(?<value>[^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class OrdersFactory(string postgres, string rabbit, string catalogUrl) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Orders", postgres);
            builder.UseSetting("ConnectionStrings:RabbitMq", rabbit);
            builder.UseSetting("Orders:Persistence:AutoCreateMessageStorage", "true");
            builder.UseSetting("Catalog:BaseAddress", catalogUrl);
            builder.UseSetting("ServiceTokens:SigningKey", _signingKey);
        }
    }

    /// <summary>The BFF with its typed clients pointed at the in-memory Orders.Api server.</summary>
    private sealed class BffFactory(string rabbit, WebApplicationFactory<Program> orders) : WebApplicationFactory<OrdersBff::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:RabbitMq", rabbit);
            builder.UseSetting("Bff:Orders:BaseAddress", "http://orders.test");
            builder.UseSetting("Bff:Events:HeartbeatInterval", "00:00:01");
            builder.UseSetting("Bff:DevLogin:Enabled", "true");
            builder.UseSetting("Bff:DevLogin:ShopperPassword", "shopper-password");
            builder.UseSetting("Bff:DevLogin:AdminPassword", "admin-password");
            builder.UseSetting("ServiceTokens:SigningKey", _signingKey);
            builder.ConfigureServices(services =>
                services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => orders.Server.CreateHandler())));
        }
    }

    /// <summary>Reads Server-Sent Events: one <c>id</c>/<c>event</c>/<c>data</c> block per event.</summary>
    private sealed class EventStreamReader(HttpResponseMessage response, Stream stream) : IDisposable
    {
        private readonly StreamReader _reader = new(stream);

        public async Task<(string? Id, string Data)> NextOfTypeAsync(string type, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            string? id = null, name = null, data = null;

            while (await _reader.ReadLineAsync(deadline.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    if (name == type)
                    {
                        return (id, data!);
                    }

                    (id, name, data) = (null, null, null);
                    continue;
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                var value = colon < 0 ? string.Empty : line[(colon + 1)..].TrimStart(' ');
                switch (colon < 0 ? line : line[..colon])
                {
                    case "id": id = value; break;
                    case "event": name = value; break;
                    case "data": data = value; break;
                }
            }

            throw new EndOfStreamException("The event stream ended.");
        }

        public void Dispose()
        {
            _reader.Dispose();
            response.Dispose();
        }
    }
}
