using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Msg.Studio.Components;
using Msg.Studio.State;

namespace Msg.Studio;

/// <summary>Hosts msg studio on localhost: a Blazor Server app over the same engine as the CLI.</summary>
public static class StudioServer
{
    public static WebApplication Build(StudioOptions options, int port, string[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args ?? [],
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Framework assets (blazor.web.js) when running from the build output instead of a published tool.
        builder.WebHost.UseStaticWebAssets();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");   // local only: the Studio writes to your disk
        builder.Logging.SetMinimumLevel(LogLevel.Error);   // keep the console clean; the UI reports problems

        builder.Services.AddSingleton(options);
        builder.Services.AddScoped<StudioState>();
        builder.Services.AddSingleton<ProcessRunner>();   // disposed on shutdown: kills every process it started
        builder.Services.AddSingleton<Toolchain>();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        var app = builder.Build();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        return app;
    }

    public static async Task RunAsync(StudioOptions options, int port, bool openBrowser, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        await using var app = Build(options, port);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var url = $"http://localhost:{port}";
        await output.WriteLineAsync($"✔ msg studio en {url}  (Ctrl+C para salir)").ConfigureAwait(false);
        await output.WriteLineAsync($"  sistema: {Path.GetFullPath(options.Workspace)}").ConfigureAwait(false);

        if (openBrowser)
        {
            TryOpenBrowser(url);
        }

        await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No default browser (e.g. a server or a container): the URL is printed anyway.
        }
    }
}
