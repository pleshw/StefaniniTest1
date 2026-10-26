using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Orders.Domain;

namespace Orders.IntegrationTests;

/// <summary>The admin order actions through the real Orders.Api pipeline over in-memory stores (no Docker).</summary>
public sealed class AdminOrderActionEndpointTests : IAsyncDisposable
{
    private const string _admin = "admin-1";
    private const string _shopper = "11111111-1111-1111-1111-111111111111";

    private readonly OrdersApiFactory _factory = new();
    private readonly HttpClient _client;

    public AdminOrderActionEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("confirm", "Confirmed")]
    [InlineData("hold", "OnHold")]
    [InlineData("block", "Blocked")]
    public async Task Post_ActionOnAPendingOrder_Returns200WithTheNewStatusAndETag(string action, string status)
    {
        var order = await PlaceOrder();

        var response = await Act(order.Id, action, ifMatch: "\"0\"", reason: "Because");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        body.GetProperty("status").GetString().ShouldBe(status);
        body.GetProperty("rowVersion").GetInt32().ShouldBe(1);
        var stored = (await _factory.Orders.GetByIdAsync(order.Id, Token))!;
        stored.Status.ToString().ShouldBe(status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Post_Hold_RecordsTheAdminAsActorWithTheReasonInTheHistoryTheEventLogAndTheOutbox()
    {
        var order = await PlaceOrder();

        await Act(order.Id, "hold", ifMatch: "\"0\"", reason: "Call the customer");

        var history = await (await Send(HttpMethod.Get, $"/api/v1/orders/{order.Id}/history", user: _admin, roles: "Admin")).Content.ReadFromJsonAsync<JsonElement>(Token);
        var last = history.EnumerateArray().Last();
        last.GetProperty("from").GetString().ShouldBe("Pending");
        last.GetProperty("to").GetString().ShouldBe("OnHold");
        last.GetProperty("reason").GetString().ShouldBe("Call the customer");
        last.GetProperty("actor").GetString().ShouldBe(_admin);
        var logged = _factory.EventLog.Entries.OfType<OrderStatusChanged>().ShouldHaveSingleItem();
        (logged.OrderId, logged.To, logged.Actor, logged.Reason).ShouldBe((order.Id, OrderStatus.OnHold, _admin, "Call the customer"));
        _factory.Events.Published.OfType<OrderStatusChanged>().ShouldHaveSingleItem().To.ShouldBe(OrderStatus.OnHold);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetOrder_ReturnsTheRowVersionAsETagAndInTheBody()
    {
        var order = await PlaceOrder();
        await Act(order.Id, "hold", ifMatch: "\"0\"", reason: "Because");

        var response = await Send(HttpMethod.Get, $"/api/v1/orders/{order.Id}", user: _admin, roles: "Admin");

        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("rowVersion").GetInt32().ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Post_FullLifeOfAnOrder_FollowsTheStateMachineOverHttp()
    {
        var order = await PlaceOrder();

        (await Act(order.Id, "hold", "\"0\"", "check")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Act(order.Id, "confirm", "\"1\"", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Act(order.Id, "cancel", "\"2\"", "customer asked")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _factory.Orders.GetByIdAsync(order.Id, Token))!.Status.ShouldBe(OrderStatus.Cancelled);
        _factory.Events.Published.OfType<OrderStatusChanged>().Select(change => change.To).ShouldBe([OrderStatus.OnHold, OrderStatus.Confirmed, OrderStatus.Cancelled]);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("OnHold", "hold")]
    [InlineData("OnHold", "cancel")]
    [InlineData("Pending", "cancel")]
    [InlineData("Confirmed", "confirm")]
    [InlineData("Confirmed", "hold")]
    [InlineData("Confirmed", "block")]
    [InlineData("Blocked", "confirm")]
    [InlineData("Blocked", "hold")]
    [InlineData("Blocked", "block")]
    [InlineData("Blocked", "cancel")]
    [InlineData("Cancelled", "confirm")]
    [InlineData("Cancelled", "cancel")]
    public async Task Post_ForbiddenTransition_Returns409InvalidTransitionAndChangesNothing(string status, string action)
    {
        var order = await PlaceOrder();
        var version = await MoveTo(order.Id, status);

        var response = await Act(order.Id, action, $"\"{version}\"", "Because");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        problem.GetProperty("type").GetString().ShouldBe("invalid-transition");
        problem.GetProperty("status").GetInt32().ShouldBe(409);
        var stored = (await _factory.Orders.GetByIdAsync(order.Id, Token))!;
        stored.Status.ToString().ShouldBe(status);
        stored.RowVersion.ShouldBe(version);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Post_StaleIfMatch_Returns412AndChangesNothing()
    {
        var order = await PlaceOrder();
        await Act(order.Id, "hold", "\"0\"", "check");

        var response = await Act(order.Id, "confirm", "\"0\"", null);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await _factory.Orders.GetByIdAsync(order.Id, Token))!.Status.ShouldBe(OrderStatus.OnHold);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Post_WithoutIfMatch_Returns428()
    {
        var order = await PlaceOrder();

        var response = await Act(order.Id, "confirm", ifMatch: null, reason: null);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        (await _factory.Orders.GetByIdAsync(order.Id, Token))!.Status.ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Post_MalformedIfMatch_Returns400()
    {
        var order = await PlaceOrder();

        (await Act(order.Id, "confirm", "zero", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("hold", null)]
    [InlineData("hold", "")]
    [InlineData("block", "  ")]
    [InlineData("cancel", null)]
    public async Task Post_HoldBlockOrCancelWithoutAReason_Returns400(string action, string? reason)
    {
        var order = await PlaceOrder();

        var response = await Act(order.Id, action, "\"0\"", reason);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadAsStringAsync(Token)).ShouldContain("reason");
        (await _factory.Orders.GetByIdAsync(order.Id, Token))!.Status.ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Post_ConfirmWithoutABody_Works()
    {
        var order = await PlaceOrder();

        var response = await Act(order.Id, "confirm", "\"0\"", reason: null, withBody: false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Post_UnknownOrder_Returns404()
    {
        (await Act(Guid.NewGuid(), "confirm", "\"0\"", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("confirm")]
    [InlineData("hold")]
    [InlineData("block")]
    [InlineData("cancel")]
    public async Task Post_SignedInWithoutTheAdminRole_Returns403AndChangesNothing(string action)
    {
        var order = await PlaceOrder();

        var response = await Send(HttpMethod.Post, $"/api/v1/admin/orders/{order.Id}/{action}", """{"reason":"x"}""", user: _shopper, ifMatch: "\"0\"");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _factory.Orders.GetByIdAsync(order.Id, Token))!.Status.ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Post_Anonymous_Returns401()
    {
        var order = await PlaceOrder();

        var response = await Send(HttpMethod.Post, $"/api/v1/admin/orders/{order.Id}/confirm", null, user: null, ifMatch: "\"0\"");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Post_TwoAdminsWithTheSameIfMatch_OneWinsAndTheOtherGets412()
    {
        var order = await PlaceOrder();

        var responses = await Task.WhenAll(Act(order.Id, "confirm", "\"0\"", null), Act(order.Id, "block", "\"0\"", "fraud"));

        responses.Select(response => response.StatusCode).Order().ShouldBe([HttpStatusCode.OK, HttpStatusCode.PreconditionFailed]);
        _factory.Events.Published.OfType<OrderStatusChanged>().ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OpenApi_ListsTheFourActions()
    {
        var document = await _client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Token);
        var paths = document.GetProperty("paths");

        foreach (var action in new[] { "confirm", "hold", "block", "cancel" })
        {
            paths.GetProperty($"/api/v1/admin/orders/{{id}}/{action}").TryGetProperty("post", out _).ShouldBeTrue(action);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    /// <summary>Walks a new order to <paramref name="status"/> through the domain, as earlier admin or Worker actions would have.</summary>
    private async Task<int> MoveTo(Guid id, string status)
    {
        var order = (await _factory.Orders.GetByIdAsync(id, Token))!;
        var expected = order.RowVersion;
        var now = DateTimeOffset.UtcNow;

        switch (status)
        {
            case "OnHold":
                order.Hold("admin-0", "check", now);
                break;
            case "Confirmed":
                order.Confirm("admin-0", now);
                break;
            case "Blocked":
                order.Block("admin-0", "fraud", now);
                break;
            case "Cancelled":
                order.Confirm("admin-0", now);
                order.Cancel("admin-0", "customer asked", now);
                break;
        }

        await _factory.Orders.UpdateAsync(order, expected, Token);

        return order.RowVersion;
    }

    private async Task<(Guid Id, int RowVersion)> PlaceOrder()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent("""{"currency":"BRL","items":[{"sku":"KB-1","quantity":1}]}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, _shopper);
        request.Headers.Add(TestAuthHandler.NameHeader, "Ana Souza");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await _client.SendAsync(request, Token);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);

        return (body.GetProperty("id").GetGuid(), body.GetProperty("rowVersion").GetInt32());
    }

    private Task<HttpResponseMessage> Act(Guid id, string action, string? ifMatch, string? reason, bool withBody = true) =>
        Send(
            HttpMethod.Post,
            $"/api/v1/admin/orders/{id}/{action}",
            withBody ? JsonSerializer.Serialize(new { reason }) : null,
            user: _admin,
            ifMatch: ifMatch,
            roles: "Admin");

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body = null, string? user = null, string? ifMatch = null, string? roles = null)
    {
        using var request = new HttpRequestMessage(method, path);

        if (user is not null)
        {
            request.Headers.Add(TestAuthHandler.UserHeader, user);
            request.Headers.Add(TestAuthHandler.NameHeader, "Test User");
        }

        if (roles is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await _client.SendAsync(request, Token);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
