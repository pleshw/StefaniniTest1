using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
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

    public const string SigningKey = "bff-tests-service-token-signing-key-0123456789";
    public const string ShopperPassword = "shopper-test-password";
    public const string AdminPassword = "admin-test-password";

    internal RecordingHandler Downstream { get; } = new();

    /// <summary>Set before the first request; on by default, as in compose.</summary>
    public bool DevLoginEnabled { get; set; } = true;

    /// <summary>A client on https (the session cookie is Secure) that keeps cookies and does not follow redirects.</summary>
    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>Signs <paramref name="user"/> in through the login form, so the client carries the session cookie.</summary>
    public static async Task<HttpResponseMessage> SignInAsync(HttpClient browser, string user, string password, string? returnUrl = null)
    {
        var form = await browser.GetStringAsync("/bff/login", TestContext.Current.CancellationToken);
        var token = System.Text.RegularExpressions.Regex.Match(form, "name=\"(?<name>[^\"]+)\" type=\"hidden\" value=\"(?<value>[^\"]+)\"");
        var fields = new Dictionary<string, string>
        {
            [token.Groups["name"].Value] = token.Groups["value"].Value,
            ["username"] = user,
            ["password"] = password,
        };

        if (returnUrl is not null)
        {
            fields["returnUrl"] = returnUrl;
        }

        return await browser.PostAsync("/bff/login", new FormUrlEncodedContent(fields), TestContext.Current.CancellationToken);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Bff:Orders:BaseAddress", OrdersAddress);
        builder.UseSetting("Bff:Catalog:BaseAddress", CatalogAddress);
        builder.UseSetting("Bff:MaxRequestBodySize", "1024");
        builder.UseSetting("ServiceTokens:SigningKey", SigningKey);
        builder.UseSetting("Bff:DevLogin:Enabled", DevLoginEnabled ? "true" : "false");
        builder.UseSetting("Bff:DevLogin:ShopperPassword", ShopperPassword);
        builder.UseSetting("Bff:DevLogin:AdminPassword", AdminPassword);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IForwarderHttpClientFactory>();
            services.AddSingleton<IForwarderHttpClientFactory>(new RecordingForwarderClientFactory(Downstream));

            // The typed clients reach the same fake services; their retries wait 1 ms instead of seconds.
            services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => new SharedHandler(Downstream)));
            services.PostConfigureAll<HttpStandardResilienceOptions>(resilience =>
            {
                resilience.Retry.Delay = TimeSpan.FromMilliseconds(1);
                resilience.Retry.UseJitter = false;
            });
        });
    }

    /// <summary>Lets HttpClientFactory rotate and dispose its handlers without disposing the shared recorder.</summary>
    private sealed class SharedHandler(RecordingHandler handler) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(handler, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _invoker.SendAsync(request, cancellationToken);
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
