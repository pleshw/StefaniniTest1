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
        await ImportDefinitionsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _management.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Stops the RabbitMQ application inside the container, so every client loses its connection. The container keeps
    /// running: stopping it would give it new host ports, and the hosts under test could never reconnect.
    /// </summary>
    public async Task StopBrokerAsync(CancellationToken cancellationToken) =>
        (await _container.ExecAsync(["rabbitmqctl", "stop_app"], cancellationToken)).ExitCode.ShouldBe(0);

    /// <summary>Starts the RabbitMQ application again and waits until it accepts connections; durable queues keep their messages.</summary>
    public async Task StartBrokerAsync(CancellationToken cancellationToken)
    {
        (await _container.ExecAsync(["rabbitmqctl", "start_app"], cancellationToken)).ExitCode.ShouldBe(0);
        (await _container.ExecAsync(["rabbitmqctl", "await_startup"], cancellationToken)).ExitCode.ShouldBe(0);
    }

    /// <summary>Loads <c>deploy/rabbitmq/definitions.json</c>, so the tests run on the topology and policies compose starts with.</summary>
    private async Task ImportDefinitionsAsync()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "deploy", "rabbitmq", "definitions.json")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("deploy/rabbitmq/definitions.json was not found above the test output directory");
        using var content = new StringContent(
            await File.ReadAllTextAsync(Path.Combine(directory.FullName, "deploy", "rabbitmq", "definitions.json")), Encoding.UTF8, "application/json");
        (await _management.PostAsync("/api/definitions", content)).EnsureSuccessStatusCode();
    }

    /// <summary>Declares a durable queue (RabbitMQ 4 refuses non-durable, non-exclusive ones) and binds it to an exchange with a routing key (retries while the exchange is being declared).</summary>
    public async Task BindQueueAsync(string queue, string exchange, string routingKey, CancellationToken cancellationToken)
    {
        (await _management.PutAsJsonAsync($"/api/queues/{_defaultVhost}/{queue}", new { durable = true, auto_delete = false }, cancellationToken)).EnsureSuccessStatusCode();

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

    /// <summary>Publishes a copy of <paramref name="message"/> to an exchange: same payload, message id, type and headers.</summary>
    public Task RepublishAsync(string exchange, QueuedMessage message, CancellationToken cancellationToken) =>
        PublishAsync(exchange, message.RoutingKey, message.Properties, message.Payload, cancellationToken);

    /// <summary>Publishes a message to an exchange and checks that some queue received it.</summary>
    public async Task PublishAsync(string exchange, string routingKey, object properties, string payload, CancellationToken cancellationToken)
    {
        var response = await _management.PostAsJsonAsync(
            $"/api/exchanges/{_defaultVhost}/{exchange}/publish",
            new { properties, routing_key = routingKey, payload, payload_encoding = "string" },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("routed").GetBoolean().ShouldBeTrue();
    }

    /// <summary>Removes every message from a queue that exists.</summary>
    public async Task PurgeAsync(string queue, CancellationToken cancellationToken)
    {
        var response = await _management.DeleteAsync($"/api/queues/{_defaultVhost}/{queue}/contents", cancellationToken);

        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// How many messages are waiting in a queue, up to 100. It reads them with a get that puts them back: the queue totals
    /// of the management API are sampled every few seconds and lag behind.
    /// </summary>
    public async Task<int> CountAsync(string queue, CancellationToken cancellationToken)
    {
        var response = await _management.PostAsJsonAsync(
            $"/api/queues/{_defaultVhost}/{queue}/get", new { count = 100, ackmode = "ack_requeue_true", encoding = "auto" }, cancellationToken);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetArrayLength();
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

    public JsonElement Properties => message.GetProperty("properties");
}
