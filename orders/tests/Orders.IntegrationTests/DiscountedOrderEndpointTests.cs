using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Orders.Domain.Discounts;

namespace Orders.IntegrationTests;

/// <summary>Discounts on create and quote through the real Orders.Api pipeline over in-memory stores (no Docker).</summary>
public sealed class DiscountedOrderEndpointTests : IAsyncDisposable
{
    private const string _cart = """{"currency":"BRL","items":[{"sku":"KB-1","quantity":2},{"sku":"MS-1","quantity":1}]}""";

    private static readonly DateTimeOffset _from = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly OrdersApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _shopper = Guid.NewGuid().ToString();

    public DiscountedOrderEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_ActiveRule_ReturnsTheDiscountedTotalsAndStoresTheRule()
    {
        var rule = Rule("""{ "percent": 10, "appliesTo": "order" }""");
        _factory.DiscountRules.Rules.Add(rule);

        var response = await Send("/api/v1/orders", Guid.NewGuid().ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var order = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        order.GetProperty("subtotal").GetDecimal().ShouldBe(49.98m);
        order.GetProperty("discountTotal").GetDecimal().ShouldBe(5.00m);
        order.GetProperty("total").GetDecimal().ShouldBe(44.98m);
        order.GetProperty("items").EnumerateArray().Sum(item => item.GetProperty("lineDiscount").GetDecimal()).ShouldBe(5.00m);
        _factory.Discounts.Stored.ShouldHaveSingleItem().Discount.RuleId.ShouldBe(rule.Id);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostQuote_ActiveRule_EqualsTheCreatedOrder()
    {
        _factory.DiscountRules.Rules.Add(Rule("""{ "amounts": [ { "currency": "BRL", "amount": 7.5 } ] }""", DiscountType.FixedAmount));

        var quote = await (await Send("/api/v1/orders/quote", key: null)).Content.ReadFromJsonAsync<JsonElement>(Token);
        var order = await (await Send("/api/v1/orders", Guid.NewGuid().ToString())).Content.ReadFromJsonAsync<JsonElement>(Token);

        quote.GetProperty("discountTotal").GetDecimal().ShouldBe(7.5m);
        foreach (var property in new[] { "subtotal", "discountTotal", "total", "items" })
        {
            quote.GetProperty(property).GetRawText().ShouldBe(order.GetProperty(property).GetRawText(), property);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_OncePerClientRuleUsedByAnEarlierOrder_PlacesTheOrderWithoutThatDiscount()
    {
        _factory.DiscountRules.Rules.Add(Rule("""{ "percent": 10, "appliesTo": "order" }""", oncePerClient: true));
        var first = await Send("/api/v1/orders", Guid.NewGuid().ToString());

        var second = await Send("/api/v1/orders", Guid.NewGuid().ToString());

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        var order = await second.Content.ReadFromJsonAsync<JsonElement>(Token);
        order.GetProperty("discountTotal").GetDecimal().ShouldBe(0m);
        order.GetProperty("total").GetDecimal().ShouldBe(order.GetProperty("subtotal").GetDecimal());
        _factory.Discounts.Stored.ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostOrder_ReplayAfterTheRuleChanged_ReturnsTheFirstResponseWithoutRecalculating()
    {
        _factory.DiscountRules.Rules.Add(Rule("""{ "percent": 10, "appliesTo": "order" }"""));
        var key = Guid.NewGuid().ToString();
        var first = await (await Send("/api/v1/orders", key)).Content.ReadAsStringAsync(Token);

        _factory.DiscountRules.Rules.Clear();
        _factory.DiscountRules.Rules.Add(Rule("""{ "percent": 30, "appliesTo": "order" }"""));
        var replay = await Send("/api/v1/orders", key);

        replay.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await replay.Content.ReadAsStringAsync(Token)).ShouldBe(first);
        _factory.Discounts.Stored.Count.ShouldBe(1);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<HttpResponseMessage> Send(string path, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(_cart, Encoding.UTF8, "application/json") };
        request.Headers.Add(TestAuthHandler.UserHeader, _shopper);
        request.Headers.Add(TestAuthHandler.NameHeader, "Ana Souza");

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await _client.SendAsync(request, Token);
    }

    private static DiscountRule Rule(string parameters, DiscountType type = DiscountType.Percentage, bool oncePerClient = false) =>
        DiscountRule.Create(Guid.NewGuid(), 1, "rule", type, parameters, "[]", 10, false, oncePerClient, _from, null, true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
