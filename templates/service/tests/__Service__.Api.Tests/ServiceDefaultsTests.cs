using System.Net;
using System.Net.Http.Json;
// @stratum:if !auth
using {{Service}}.Api.Endpoints;
// @stratum:end

namespace {{Service}}.Api.Tests;

/// <summary>
/// Guards the contract every Stratum service must keep: health endpoints, ProblemDetails, correlation id.
/// If one of these fails, the service no longer behaves like the rest of the platform.
/// </summary>
public sealed class ServiceDefaultsTests(ApiFactory factory) : IClassFixture<ApiFactory>
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

        // @stratum:if auth
        // Fallback policy applies to every request, even unmatched routes: anonymous callers get 401
        // instead of 404, so they can't discover which routes exist.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // @stratum:else
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // @stratum:end
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
    // @stratum:if openApi

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar")]
    public async Task ApiDocumentationIsAvailableInDevelopment(string path)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}");
    }
    // @stratum:end
    // @stratum:if auth

    [Fact]
    public async Task ApiRequiresAuthentication()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/v1/ping", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    // @stratum:else

    [Fact]
    public async Task PingReturnsServiceInfo()
    {
        using var client = factory.CreateClient();
        var ping = await client.GetFromJsonAsync<PingResponse>(new Uri("/api/v1/ping", UriKind.Relative));

        Assert.NotNull(ping);
        Assert.Equal("{{service-kebab}}", ping.Service);
    }
    // @stratum:end
}
