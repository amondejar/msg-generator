using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Msg.ServiceDefaults.Tests;

public sealed class ServiceDefaultsTests
{
    [Fact]
    public async Task LivenessIgnoresDependencyChecksButReadinessDoesNot()
    {
        await using var app = await TestApp.StartAsync(builder =>
            builder.Services.AddHealthChecks()
                .AddCheck("database", () => HealthCheckResult.Unhealthy("down")));
        using var client = app.GetTestClient();

        var live = await client.GetAsync(new Uri(MsgServiceDefaultsExtensions.LivenessPath, UriKind.Relative));
        var ready = await client.GetAsync(new Uri(MsgServiceDefaultsExtensions.ReadinessPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public async Task UnknownRouteReturnsProblemDetailsWithCorrelationId()
    {
        await using var app = await TestApp.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/nope", UriKind.Relative));
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "abc-123");

        var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("abc-123", body.RootElement.GetProperty("correlationId").GetString());
        Assert.True(body.RootElement.TryGetProperty("traceId", out _));
        Assert.Equal("GET /nope", body.RootElement.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task UnhandledExceptionReturnsProblemDetailsWithoutLeakingDetails()
    {
        await using var app = await TestApp.StartAsync(configureApp: app =>
            app.MapGet("/boom", string () => throw new InvalidOperationException("secret connection string")));
        using var client = app.GetTestClient();

        var response = await client.GetAsync(new Uri("/boom", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("secret", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("valid-id_1.2", true)]
    [InlineData("<script>", false)]
    [InlineData("has space", false)]
    public async Task CorrelationIdIsEchoedOnlyWhenSafe(string incoming, bool echoed)
    {
        await using var app = await TestApp.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(MsgServiceDefaultsExtensions.LivenessPath, UriKind.Relative));
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, incoming);

        var response = await client.SendAsync(request);
        var returned = Assert.Single(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName));

        Assert.False(string.IsNullOrEmpty(returned));
        Assert.Equal(echoed, returned == incoming);
    }

    [Fact]
    public async Task InvalidOptionsFailAtStartup()
    {
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            TestApp.StartAsync(builder => builder.Services.AddValidatedOptions<SampleOptions>("Sample")));

        Assert.Contains("Name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidOptionsAreBound()
    {
        await using var app = await TestApp.StartAsync(
            builder => builder.Services.AddValidatedOptions<SampleOptions>("Sample"),
            settings: new() { ["Sample:Name"] = "orders" });

        Assert.Equal("orders", app.Services.GetRequiredService<IOptions<SampleOptions>>().Value.Name);
    }

    private sealed class SampleOptions
    {
        [Required]
        public string Name { get; init; } = string.Empty;
    }
}
