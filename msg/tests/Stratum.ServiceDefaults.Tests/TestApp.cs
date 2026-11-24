using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Stratum.ServiceDefaults.Tests;

/// <summary>Builds a minimal in-memory app wired like a generated Stratum service.</summary>
internal static class TestApp
{
    public static async Task<WebApplication> StartAsync(
        Action<WebApplicationBuilder>? configureBuilder = null,
        Action<WebApplication>? configureApp = null,
        Dictionary<string, string?>? settings = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        if (settings is not null)
        {
            builder.Configuration.AddInMemoryCollection(settings);
        }

        builder.AddStratumServiceDefaults();
        configureBuilder?.Invoke(builder);

        var app = builder.Build();
        app.UseStratumServiceDefaults();
        configureApp?.Invoke(app);
        app.MapStratumDefaultEndpoints();

        await app.StartAsync();
        return app;
    }
}
