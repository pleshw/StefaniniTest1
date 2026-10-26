using System.Net;
using ServiceDefaults;

namespace Orders.Bff.UnitTests;

public sealed class HealthCheckCommandTests
{
    private static readonly Uri _readyUrl = new("http://localhost:8080/health/ready");

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("healthcheck")]
    [InlineData("HealthCheck")]
    public void IsRequested_FirstArgumentIsHealthcheck_ReturnsTrue(string argument)
    {
        HealthCheckCommand.IsRequested([argument]).ShouldBeTrue();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData]
    [InlineData("--urls")]
    public void IsRequested_NoHealthcheckArgument_ReturnsFalse(params string[] args)
    {
        HealthCheckCommand.IsRequested(args).ShouldBeFalse();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null, "http://localhost:8080/health/ready")]
    [InlineData("", "http://localhost:8080/health/ready")]
    [InlineData("9000", "http://localhost:9000/health/ready")]
    [InlineData("9000;9001", "http://localhost:9000/health/ready")]
    [InlineData("not-a-port", "http://localhost:8080/health/ready")]
    public void ReadyUrl_HttpPortsSetting_ResolvesFirstValidPortOrDefault(string? httpPorts, string expected)
    {
        HealthCheckCommand.ReadyUrl(httpPorts).ToString().ShouldBe(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_ReadyEndpointReturnsOk_ReturnsZero()
    {
        using var client = ClientReturning(HttpStatusCode.OK);

        var exitCode = await HealthCheckCommand.RunAsync(client, _readyUrl, TimeSpan.FromSeconds(1));

        exitCode.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_ReadyEndpointReturnsUnavailable_ReturnsOne()
    {
        using var client = ClientReturning(HttpStatusCode.ServiceUnavailable);

        var exitCode = await HealthCheckCommand.RunAsync(client, _readyUrl, TimeSpan.FromSeconds(1));

        exitCode.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_ConnectionFails_ReturnsOne()
    {
        using var client = new HttpClient(new StubHandler((_, _) => throw new HttpRequestException("refused")));

        var exitCode = await HealthCheckCommand.RunAsync(client, _readyUrl, TimeSpan.FromSeconds(1));

        exitCode.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_NoAnswerWithinTimeout_ReturnsOne()
    {
        using var client = new HttpClient(new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var exitCode = await HealthCheckCommand.RunAsync(client, _readyUrl, TimeSpan.FromMilliseconds(50));

        exitCode.ShouldBe(1);
    }

    private static HttpClient ClientReturning(HttpStatusCode statusCode) =>
        new(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode))));

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
