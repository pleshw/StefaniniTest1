using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yarp.ReverseProxy.Forwarder;

namespace Orders.Bff.UnitTests.Support;

/// <summary>
/// The real BFF pipeline with fake downstream services: every proxied request goes to <see cref="Downstream"/>, which
/// records it and answers with whatever the test set, so no network is involved.
/// </summary>
public sealed class BffFactory : WebApplicationFactory<Program>
{
    public const string OrdersAddress = "http://orders.test";
    public const string CatalogAddress = "http://catalog.test";

    internal RecordingHandler Downstream { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Bff:Orders:BaseAddress", OrdersAddress);
        builder.UseSetting("Bff:Catalog:BaseAddress", CatalogAddress);
        builder.UseSetting("Bff:MaxRequestBodySize", "1024");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IForwarderHttpClientFactory>();
            services.AddSingleton<IForwarderHttpClientFactory>(new RecordingForwarderClientFactory(Downstream));
        });
    }

    private sealed class RecordingForwarderClientFactory(RecordingHandler handler) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(handler, disposeHandler: false);
    }
}

/// <summary>Records each request it receives and answers with <see cref="Respond"/> (200 with an empty JSON object by default).</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        lock (_requests)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)), body));
        }

        return Respond(request);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body);
