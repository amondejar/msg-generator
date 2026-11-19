using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
// @msg:if !auth
using {{Service}}.Api.Endpoints;
// @msg:end

namespace {{Service}}.Api.Tests;

/// <summary>
/// Guards the contract every MSG service must keep: health endpoints, ProblemDetails, correlation id.
/// If one of these fails, the service no longer behaves like the rest of the platform.
/// </summary>
public sealed class ServiceDefaultsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpointsReturnOk(string path)
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnknownRouteReturnsProblemDetails()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync(new Uri("/does-not-exist", UriKind.Relative));

        // @msg:if auth
        // Fallback policy applies to every request, even unmatched routes: anonymous callers get 401
        // instead of 404, so they can't discover which routes exist.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // @msg:else
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // @msg:end
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task CorrelationIdIsEchoed()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live", UriKind.Relative));
        request.Headers.Add("X-Correlation-ID", "test-123");

        var response = await client.SendAsync(request);

        Assert.Equal("test-123", Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
    }
    // @msg:if auth

    [Fact]
    public async Task ApiRequiresAuthentication()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/v1/ping", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    // @msg:else

    [Fact]
    public async Task PingReturnsServiceInfo()
    {
        using var client = factory.CreateClient();
        var ping = await client.GetFromJsonAsync<PingResponse>(new Uri("/api/v1/ping", UriKind.Relative));

        Assert.NotNull(ping);
        Assert.Equal("{{service-kebab}}", ping.Service);
    }
    // @msg:end
}
