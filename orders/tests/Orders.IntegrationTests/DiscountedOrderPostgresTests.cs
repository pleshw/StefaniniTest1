using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orders.Domain.Discounts;
using Orders.Infrastructure.Persistence;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Orders.IntegrationTests;

/// <summary>Discounts end to end: real Orders.Api, PostgreSQL (Testcontainers), the redemption index and a WireMock Catalog.</summary>
public sealed class DiscountedOrderPostgresTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string _pricesBody = """
        { "items": [ { "sku": "KB-1", "name": "Keyboard", "category": "peripherals", "unitPrice": 100.00, "currency": "BRL", "version": 3, "isActive": true } ], "missing": [] }
        """;

    private static readonly DateTimeOffset _from = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;
    private readonly WireMockServer _catalog = WireMockServer.Start();
    private readonly string _shopper = Guid.NewGuid().ToString();
    private readonly Guid _rule = Guid.NewGuid();
    private WebApplicationFactory<Program> _factory = null!;

    public DiscountedOrderPostgresTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    public async ValueTask InitializeAsync()
    {
        _catalog.Given(Request.Create().WithPath("/api/v1/products/prices:query").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(_pricesBody));

        // Every other rule in the shared database is switched off, so only this test's rule applies.
        await using var context = _postgres.NewContext();
        await context.DiscountRules.ExecuteUpdateAsync(set => set.SetProperty(rule => rule.IsActive, false), Token);
        context.DiscountRules.Add(new DiscountRuleRecord
        {
            Id = _rule,
            Version = 1,
            Name = "Welcome 10%",
            Type = DiscountType.Percentage,
            Parameters = """{ "percent": 10, "appliesTo": "order" }""",
            Conditions = "[]",
            Priority = 1,
            OncePerClient = true,
            ValidFrom = _from,
            IsActive = true,
            CreatedBy = "admin-1",
            CreatedAt = _from,
        });
        await context.SaveChangesAsync(Token);

        _factory = new PostgresApiFactory(_postgres.ConnectionString, _catalog.Url!);
        _ = _factory.Services;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _catalog.Dispose();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_TwoKeysRacingForAOncePerClientRule_OneIs201AndTheOther409()
    {
        var client = _factory.CreateClient();

        var responses = await Task.WhenAll(Post(client, Guid.NewGuid().ToString()), Post(client, Guid.NewGuid().ToString()));

        responses.Select(response => response.Status).Order().ShouldBe([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        responses.Single(response => response.Status == HttpStatusCode.Conflict).Body.ShouldContain("discount-already-used");
        await using var context = _postgres.NewContext();
        (await context.DiscountRedemptions.CountAsync(redemption => redemption.DiscountRuleId == _rule, Token)).ShouldBe(1);
        (await context.OrderDiscounts.CountAsync(discount => discount.DiscountRuleId == _rule, Token)).ShouldBe(1);
        (await context.Orders.CountAsync(order => order.ClientId == Guid.Parse(_shopper), Token)).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_SameKeyAgainAfterTheRuleChanged_ReplaysWithoutRecalculating()
    {
        var client = _factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        var first = await Post(client, key);
        await using (var context = _postgres.NewContext())
        {
            await context.DiscountRules.Where(rule => rule.Id == _rule).ExecuteUpdateAsync(set => set.SetProperty(rule => rule.IsActive, false), Token);
        }

        var replay = await Post(client, key);

        first.Status.ShouldBe(HttpStatusCode.Created);
        replay.Status.ShouldBe(HttpStatusCode.Created);
        using var firstBody = JsonDocument.Parse(first.Body);
        using var replayBody = JsonDocument.Parse(replay.Body);
        replayBody.RootElement.GetProperty("discountTotal").GetDecimal().ShouldBe(10m);
        replayBody.RootElement.GetProperty("id").GetGuid().ShouldBe(firstBody.RootElement.GetProperty("id").GetGuid());
    }

    private async Task<(HttpStatusCode Status, string Body)> Post(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent("""{"currency":"BRL","items":[{"sku":"KB-1","quantity":1}]}""", Encoding.UTF8, "application/json"),
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
            builder.UseSetting("Orders:Messaging:Enabled", "false");
            builder.UseSetting("Catalog:BaseAddress", catalogUrl);
            builder.UseSetting("Catalog:Client:RetryDelay", "00:00:00.01");
            builder.ConfigureServices(services => services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
        }
    }
}
