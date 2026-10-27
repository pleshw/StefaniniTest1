using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Orders.Bff.UnitTests.Support;

namespace Orders.Bff.UnitTests;

/// <summary>Errors from Orders and Catalog reach the browser as the service wrote them.</summary>
public sealed class ProblemDetailsPassthroughTests : IAsyncDisposable
{
    private readonly BffFactory _factory = new();
    private readonly HttpClient _client;

    public ProblemDetailsPassthroughTests()
    {
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-CSRF", "1");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "invalid-transition")]
    [InlineData(HttpStatusCode.PreconditionFailed, "precondition-failed")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "unprocessable")]
    [Trait("Category", "Unit")]
    public async Task DownstreamProblem_IsReturnedUnchanged(HttpStatusCode status, string type)
    {
        var body = $$"""{"type":"{{type}}","title":"Downstream said no","status":{{(int)status}},"detail":"kept as is","traceId":"00-abc"}""";
        _factory.Downstream.Respond = _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/problem+json"),
        };

        var response = await _client.PostAsync("/bff/admin/orders/6f1c2d3e-0000-4000-8000-000000000001/confirm", JsonContent.Create(new { }), Token);

        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadAsStringAsync(Token)).ShouldBe(body);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DownstreamErrorWithoutBody_IsNotRewrittenByTheBff()
    {
        _factory.Downstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var response = await _client.GetAsync("/bff/admin/dead-letters", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(Token)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DownstreamUnreachable_Returns502ProblemDetails()
    {
        _factory.Downstream.Respond = _ => throw new HttpRequestException("connection refused");

        var response = await _client.GetAsync("/bff/admin/dead-letters", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("status").GetInt32().ShouldBe(502);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
