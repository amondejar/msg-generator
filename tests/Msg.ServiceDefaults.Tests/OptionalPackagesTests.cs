using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;

namespace Msg.ServiceDefaults.Tests;

public sealed class OptionalPackagesTests
{
    private static readonly Dictionary<string, string?> ValidAuth = new()
    {
        ["Auth:Authority"] = "https://idp.example.com/realms/test",
        ["Auth:Audience"] = "orders",
    };

    [Fact]
    public async Task AuthenticationProtectsEndpointsButNotHealthChecks()
    {
        await using var app = await TestApp.StartAsync(
            builder => builder.AddMsgAuthentication(),
            app =>
            {
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapGet("/api/secret", () => "ok");
            },
            ValidAuth);
        using var client = app.GetTestClient();

        var secret = await client.GetAsync(new Uri("/api/secret", UriKind.Relative));
        var health = await client.GetAsync(new Uri(MsgServiceDefaultsExtensions.LivenessPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, secret.StatusCode);
        Assert.Equal("application/problem+json", secret.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task AuthenticationRequiresConfiguration()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() =>
            TestApp.StartAsync(builder => builder.AddMsgAuthentication()));
    }

    [Fact]
    public async Task OpenTelemetryRegistersTracing()
    {
        await using var app = await TestApp.StartAsync(builder => builder.AddMsgOpenTelemetry("orders"));

        Assert.NotNull(app.Services.GetService<TracerProvider>());
    }

    [Fact]
    public async Task ResilienceDoesNotBreakHttpClientFactory()
    {
        await using var app = await TestApp.StartAsync(builder => builder.AddMsgResilience());

        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("any");

        Assert.NotNull(client);
    }
}
