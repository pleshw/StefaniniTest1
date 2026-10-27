using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Orders.Bff.UnitTests.Support;

namespace Orders.Bff.UnitTests;

/// <summary>The BFF refuses bodies above <c>Bff:MaxRequestBodySize</c> (1,024 bytes in these tests) before forwarding.</summary>
public sealed class RequestSizeLimitTests : IAsyncDisposable
{
    private readonly BffFactory _factory = new();
    private readonly HttpClient _client;

    public RequestSizeLimitTests()
    {
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-CSRF", "1");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BodyAboveTheLimit_Returns413ProblemDetailsAndForwardsNothing()
    {
        var response = await _client.PostAsync("/bff/admin/discount-rules", Body(1025), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("status").GetInt32().ShouldBe(413);
        _factory.Downstream.Requests.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BodyAtTheLimit_IsForwarded()
    {
        var response = await _client.PostAsync("/bff/admin/discount-rules", Body(1024), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _factory.Downstream.Requests.ShouldHaveSingleItem().Body!.Length.ShouldBe(1024);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static StringContent Body(int bytes) => new(new string('a', bytes), Encoding.ASCII, "text/plain");

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
