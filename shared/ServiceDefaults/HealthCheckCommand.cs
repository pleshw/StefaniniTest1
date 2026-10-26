using System.Globalization;

namespace ServiceDefaults;

/// <summary>
/// The <c>healthcheck</c> command-line argument. Chiseled images have no shell or curl, so the container
/// healthcheck runs the app itself, which calls its own readiness endpoint and exits 0 (ready) or 1.
/// </summary>
public static class HealthCheckCommand
{
    public const string Argument = "healthcheck";

    internal const string HttpPortsKey = "ASPNETCORE_HTTP_PORTS";
    internal const int DefaultPort = 8080;

    private static readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(3);

    public static bool IsRequested(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Argument, StringComparison.OrdinalIgnoreCase);

    public static Uri ReadyUrl(string? httpPorts)
    {
        var port = httpPorts?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0)
            .FirstOrDefault(value => value is > 0 and <= 65535) ?? 0;

        return new Uri(FormattableString.Invariant(
            $"http://localhost:{(port == 0 ? DefaultPort : port)}{ServiceDefaultsExtensions.ReadyHealthPath}"));
    }

    public static async Task<int> RunAsync(HttpClient client, Uri readyUrl, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            using var response = await client.GetAsync(readyUrl, cancellation.Token);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
    }

    public static async Task<int> RunAsync()
    {
        using var client = new HttpClient();
        var readyUrl = ReadyUrl(Environment.GetEnvironmentVariable(HttpPortsKey));
        return await RunAsync(client, readyUrl, _defaultTimeout);
    }
}
