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

/// <summary>
/// A first-order discount that is not once-per-client, so the redemption index cannot stop it: only the client lock in
/// the order transaction keeps two concurrent orders of a new client from both getting the benefit.
/// </summary>
public sealed class FirstOrderDiscountPostgresTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string _pricesBody = """
        { "items": [ { "sku": "KB-1", "name": "Keyboard", "category": "peripherals", "unitPrice": 100.00, "currency": "BRL", "version": 3, "isActive": true } ], "missing": [] }
        """;

    private static readonly DateTimeOffset _from = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;
    private readonly WireMockServer _catalog = WireMockServer.Start();
    private readonly Guid _shopper = Guid.NewGuid();
    private readonly Guid _rule = Guid.NewGuid();
    private WebApplicationFactory<Program> _factory = null!;

    public FirstOrderDiscountPostgresTests(PostgresFixture postgres)
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
            Name = "First order 10%",
            Type = DiscountType.Percentage,
            Parameters = """{ "percent": 10, "appliesTo": "order" }""",
            Conditions = """[{ "kind": "FirstOrderOfClient" }]""",
            Priority = 1,
            OncePerClient = false,
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
    public async Task PostOrder_TwoKeysRacingForANewClientsFirstOrder_OnlyOneGetsTheDiscount()
    {
        var client = _factory.CreateClient();

        var responses = await Task.WhenAll(Post(client, Guid.NewGuid().ToString()), Post(client, Guid.NewGuid().ToString()));

        responses.Select(response => response.Status).ShouldBe([HttpStatusCode.Created, HttpStatusCode.Created]);
        responses.Select(response => DiscountTotal(response.Body)).Order().ShouldBe([0m, 10m]);
        await using var context = _postgres.NewContext();
        (await context.Orders.CountAsync(order => order.ClientId == _shopper, Token)).ShouldBe(2);
        (await context.OrderDiscounts.CountAsync(discount => discount.DiscountRuleId == _rule, Token)).ShouldBe(1);
        (await context.DiscountRedemptions.CountAsync(redemption => redemption.DiscountRuleId == _rule, Token)).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_SecondOrderAfterTheFirstCommitted_GetsNoDiscount()
    {
        var client = _factory.CreateClient();

        var first = await Post(client, Guid.NewGuid().ToString());
        var second = await Post(client, Guid.NewGuid().ToString());

        DiscountTotal(first.Body).ShouldBe(10m);
        DiscountTotal(second.Body).ShouldBe(0m);
    }

    private static decimal DiscountTotal(string body)
    {
        using var document = JsonDocument.Parse(body);

        return document.RootElement.GetProperty("discountTotal").GetDecimal();
    }

    private async Task<(HttpStatusCode Status, string Body)> Post(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent("""{"currency":"BRL","items":[{"sku":"KB-1","quantity":1}]}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, _shopper.ToString());
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
