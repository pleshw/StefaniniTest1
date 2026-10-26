using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Orders.Bff.UnitTests;

public sealed class BffHealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public BffHealthTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveHealth_ServiceRunning_ReturnsOk()
    {
        var response = await _client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadyHealth_NoDependenciesRegistered_ReturnsOk()
    {
        var response = await _client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnknownRoute_NoMatchingEndpoint_ReturnsProblemDetailsJson()
    {
        var response = await _client.GetAsync("/does-not-exist", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe(404);
    }
}
