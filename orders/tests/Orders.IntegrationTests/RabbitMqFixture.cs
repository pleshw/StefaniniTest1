using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Testcontainers.RabbitMq;

namespace Orders.IntegrationTests;

/// <summary>
/// A RabbitMQ 4 container with the management plugin, as in compose. Messages are inspected through the management
/// HTTP API, so the tests need no AMQP client of their own.
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    private const string _defaultVhost = "%2F";
    private const ushort _managementPort = 15672;

    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-management").WithPortBinding(_managementPort, true).Build();
    private readonly HttpClient _management = new();

    public string AmqpUri => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _management.BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(_managementPort)}");
        _management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{RabbitMqBuilder.DefaultUsername}:{RabbitMqBuilder.DefaultPassword}")));
    }

    public async ValueTask DisposeAsync()
    {
        _management.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>Declares an auto-delete queue and binds it to an exchange with a routing key (retries while the exchange is being declared).</summary>
    public async Task BindQueueAsync(string queue, string exchange, string routingKey, CancellationToken cancellationToken)
    {
        (await _management.PutAsJsonAsync($"/api/queues/{_defaultVhost}/{queue}", new { durable = false, auto_delete = false }, cancellationToken)).EnsureSuccessStatusCode();

        for (var attempt = 0; ; attempt++)
        {
            var response = await _management.PostAsJsonAsync(
                $"/api/bindings/{_defaultVhost}/e/{exchange}/q/{queue}", new { routing_key = routingKey }, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return;
            }

            if (attempt >= 20)
            {
                response.EnsureSuccessStatusCode();
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    /// <summary>Takes up to <paramref name="count"/> messages off the queue (acknowledging them).</summary>
    public async Task<IReadOnlyList<QueuedMessage>> GetMessagesAsync(string queue, int count, CancellationToken cancellationToken)
    {
        var response = await _management.PostAsJsonAsync(
            $"/api/queues/{_defaultVhost}/{queue}/get", new { count, ackmode = "ack_requeue_false", encoding = "auto" }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var messages = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        return messages.EnumerateArray().Select(message => new QueuedMessage(message)).ToList();
    }

    /// <summary>Waits until at least <paramref name="count"/> messages could be taken off the queue.</summary>
    public async Task<IReadOnlyList<QueuedMessage>> WaitForMessagesAsync(string queue, int count, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var found = new List<QueuedMessage>();
        var deadline = DateTime.UtcNow + timeout;

        while (found.Count < count && DateTime.UtcNow < deadline)
        {
            found.AddRange(await GetMessagesAsync(queue, count - found.Count, cancellationToken));

            if (found.Count < count)
            {
                await Task.Delay(250, cancellationToken);
            }
        }

        return found;
    }
}

/// <summary>One message as the management API shows it.</summary>
public sealed class QueuedMessage(JsonElement message)
{
    public string RoutingKey => message.GetProperty("routing_key").GetString()!;

    public string Payload => message.GetProperty("payload").GetString()!;

    public string? MessageId => Properties.TryGetProperty("message_id", out var id) ? id.GetString() : null;

    public JsonElement Headers => Properties.TryGetProperty("headers", out var headers) ? headers : default;

    public JsonElement Json => JsonDocument.Parse(Payload).RootElement.Clone();

    public string? Header(string name) =>
        Headers.ValueKind == JsonValueKind.Object && Headers.TryGetProperty(name, out var value) ? value.ToString() : null;

    private JsonElement Properties => message.GetProperty("properties");
}
