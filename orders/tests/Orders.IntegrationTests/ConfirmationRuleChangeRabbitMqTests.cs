extern alias OrdersWorker;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orders.Application.Ordering;
using Orders.Contracts;
using Orders.Domain;
using Orders.Infrastructure.Persistence;

namespace Orders.IntegrationTests;

/// <summary>
/// An admin changes a confirmation rule through the real Orders.Api and the real Orders.Worker uses the new version for the
/// next order it decides (PostgreSQL and RabbitMQ with Testcontainers). The Worker's rule cache is short, as it must be:
/// the API can only empty its own cache.
/// </summary>
public sealed class ConfirmationRuleChangeRabbitMqTests : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan _workerCache = TimeSpan.FromMilliseconds(200);

    private readonly PostgresFixture _postgres;
    private readonly RabbitMqFixture _rabbit;
    private WebApplicationFactory<Program>? _api;
    private WebApplicationFactory<OrdersWorker::Program>? _worker;

    public ConfirmationRuleChangeRabbitMqTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    {
        _postgres = postgres;
        _rabbit = rabbit;
    }

    public async ValueTask InitializeAsync()
    {
        await _rabbit.PurgeAsync("orders.worker.placed", Token);
        await using var context = _postgres.NewContext();
        await context.ConfirmationRules.ExecuteUpdateAsync(set => set.SetProperty(rule => rule.IsActive, false), Token);
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DO $$ BEGIN
              IF to_regclass('wolverine.wolverine_incoming_envelopes') IS NOT NULL THEN
                DELETE FROM wolverine.wolverine_incoming_envelopes;
                DELETE FROM wolverine.wolverine_dead_letters;
              END IF;
            END $$;
            """;
        await command.ExecuteNonQueryAsync(Token);

        _api = new ApiFactory(_postgres.ConnectionString, _rabbit.AmqpUri);
        _worker = new WorkerFactory(_postgres.ConnectionString, _rabbit.AmqpUri);
        _ = _api.Services;
        _ = _worker.Services;
    }

    public async ValueTask DisposeAsync()
    {
        if (_worker is not null)
        {
            await _worker.DisposeAsync();
        }

        if (_api is not null)
        {
            await _api.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Worker_AfterAnAdminReplacesOrDeactivatesARule_UsesTheNewVersionForTheNextOrder()
    {
        var client = _api!.CreateClient();
        var created = await Admin(client, HttpMethod.Post, "/api/v1/admin/confirmation-rules", Body("Large BRL order", 5000));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
        await Task.Delay(_workerCache * 3, Token);

        var held = await Decide(6000m);
        var replaced = await Admin(client, HttpMethod.Put, $"/api/v1/admin/confirmation-rules/{id}", Body("Large BRL order", 9000), "\"1\"");
        await Task.Delay(_workerCache * 3, Token);
        var confirmedUnderTheNewThreshold = await Decide(6000m);
        var heldUnderTheNewThreshold = await Decide(10000m);
        var deactivated = await Admin(client, HttpMethod.Post, $"/api/v1/admin/confirmation-rules/{id}/deactivate", null, "\"2\"");
        await Task.Delay(_workerCache * 3, Token);
        var confirmedWithoutTheRule = await Decide(10000m);

        replaced.StatusCode.ShouldBe(HttpStatusCode.OK);
        deactivated.StatusCode.ShouldBe(HttpStatusCode.OK);
        held.ShouldBe((OrderStatus.OnHold, "Large BRL order"));
        confirmedUnderTheNewThreshold.Status.ShouldBe(OrderStatus.Confirmed);
        heldUnderTheNewThreshold.ShouldBe((OrderStatus.OnHold, "Large BRL order"));
        confirmedWithoutTheRule.Status.ShouldBe(OrderStatus.Confirmed);
    }

    private static string Body(string name, int amount) =>
        $$"""{ "name": "{{name}}", "conditions": [ { "kind": "TotalAbove", "amount": {{amount}}, "currency": "BRL" } ], "priority": 1 }""";

    /// <summary>Places an order of this total in the database, announces it and waits for the Worker's decision.</summary>
    private async Task<(OrderStatus Status, string? Reason)> Decide(decimal unitPrice)
    {
        var id = Guid.NewGuid();
        var placed = DateTimeOffset.UtcNow;
        var order = Order.Place(
            id, OrderNumbers.For(id, placed), ClientRef.Create(Guid.NewGuid(), "Ana"), "shopper-1", OrderChannel.Shop,
            [OrderItem.Create("KB-1", "Keyboard", Money.Create(unitPrice, Currency.Create("BRL")), 1, 1)], placed);
        await using (var context = _postgres.NewContext())
        {
            context.Orders.Add(order.ToRecord());
            await context.SaveChangesAsync(Token);
        }

        await _rabbit.PublishAsync(
            "orders.events",
            "order.placed",
            new
            {
                type = "Orders.Contracts.OrderPlacedV1",
                message_id = Guid.NewGuid().ToString(),
                content_type = "application/json",
                headers = new Dictionary<string, string> { ["occurred-at"] = placed.ToString("O") },
            },
            JsonSerializer.Serialize(new OrderPlacedV1(id, order.Number, order.Client.Id, "shop", "BRL", order.Total.Amount, 1, placed), _json),
            Token);

        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            await using var context = _postgres.NewContext();
            var stored = await new OrderRepository(context).GetByIdAsync(id, Token);

            if (stored!.Status != OrderStatus.Pending)
            {
                return (stored.Status, stored.StatusHistory[^1].Reason);
            }

            await Task.Delay(200, Token);
        }

        return (OrderStatus.Pending, null);
    }

    private static async Task<HttpResponseMessage> Admin(HttpClient client, HttpMethod method, string path, string? body, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TestAuthHandler.UserHeader, "admin-1");
        request.Headers.Add(TestAuthHandler.RolesHeader, "Admin");

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Token);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class ApiFactory(string postgres, string rabbit) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Orders", postgres);
            builder.UseSetting("ConnectionStrings:RabbitMq", rabbit);
            builder.UseSetting("Orders:Persistence:AutoCreateMessageStorage", "true");
            builder.ConfigureServices(services => services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
        }
    }

    private sealed class WorkerFactory(string postgres, string rabbit) : WebApplicationFactory<OrdersWorker::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Orders", postgres);
            builder.UseSetting("ConnectionStrings:RabbitMq", rabbit);
            builder.UseSetting("Orders:Persistence:AutoCreateMessageStorage", "true");
            builder.UseSetting("Orders:Worker:ScheduledJobPollingInterval", "00:00:00.1");
            builder.UseSetting("Orders:Confirmation:CacheDuration", _workerCache.ToString("c", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
