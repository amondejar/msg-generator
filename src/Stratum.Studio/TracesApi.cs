using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Stratum.Core.Generation;
using Stratum.Core.Model;
using Stratum.Studio.Localization;
using Stratum.Studio.State;

namespace Stratum.Studio;

/// <summary>
/// Event traces: a window of its own that asks every running service of the system what it sent and received
/// (GET /_stratum/messaging, mapped in Development by StratumWiring.g.cs) and joins it by message id
/// (<see cref="EventTraces"/>): from the request that published an event to each service that handled it.
/// </summary>
internal static class TracesApi
{
    public sealed record LinksDto(string? Dashboard, string? KafkaUi);

    public sealed record ServiceDto(string Name, string Directory, string Url, string State, string? Message, string? Broker, IReadOnlyList<string> Problems, IReadOnlyList<PublishableDto> Publishes);

    /// <summary>An event the service publishes, with sample data for a test event.</summary>
    public sealed record PublishableDto(string Topic, string Event, string Sample);

    public sealed record ResendRequest(string Service, string MessageId);

    public sealed record PublishRequest(string Service, string Topic, string Payload);

    /// <summary>What the service answered (or why it couldn't be asked).</summary>
    public sealed record ActionDto(bool Ok, string Message, string? MessageId);

    public sealed record ReportDto(string System, DateTime GeneratedAt, IReadOnlyList<ServiceDto> Services, IReadOnlyList<EventTrace> Events, LinksDto Links);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>A load reads every page of the publisher: it takes longer than a probe.</summary>
    private static readonly HttpClient LoadHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new UtcDateTimeConverter() } };

    public static void Map(WebApplication app, StudioOptions options)
    {
        app.MapGet("/traces", () => Results.Content(DesignerApi.Render("Stratum.Studio.Designer.traces.html"), "text/html; charset=utf-8"));
        app.MapGet("/traces/api/report", async (int? minutes, CancellationToken cancellationToken) =>
            Results.Json(await ReportAsync(options.Workspace, Math.Clamp(minutes ?? 60, 1, 60 * 24 * 7), cancellationToken).ConfigureAwait(false), Json));
        app.MapPost("/traces/api/resend", async (ResendRequest request, CancellationToken cancellationToken) =>
            Results.Json(Guid.TryParse(request.MessageId, out var id)
                ? await CallAsync(options.Workspace, request.Service, $"/_stratum/messaging/resend/{id}", null, cancellationToken).ConfigureAwait(false)
                : new ActionDto(false, I18n.L("Id de mensaje no válido.", "Invalid message id."), null), Json));
        app.MapPost("/traces/api/publish", async (PublishRequest request, CancellationToken cancellationToken) =>
        {
            JsonObject? payload;
            try
            {
                payload = JsonNode.Parse(string.IsNullOrWhiteSpace(request.Payload) ? "{}" : request.Payload) as JsonObject;
            }
            catch (JsonException ex)
            {
                return Results.Json(new ActionDto(false, I18n.L("El JSON no es válido: ", "The JSON isn't valid: ") + ex.Message, null), Json);
            }

            if (payload is null)
            {
                return Results.Json(new ActionDto(false, I18n.L("Los datos tienen que ser un objeto JSON { … }.", "The data must be a JSON object { … }."), null), Json);
            }

            var body = new JsonObject { ["topic"] = request.Topic, ["payload"] = payload };
            return Results.Json(await CallAsync(options.Workspace, request.Service, "/_stratum/messaging/publish", body, cancellationToken).ConfigureAwait(false), Json);
        });
    }

    /// <summary>
    /// Initial load of a replica again, in the running service (POST /_stratum/replicas/{publisher}/{entity}/load,
    /// Development): reads the publisher's list and brings the copy up to date.
    /// </summary>
    public static async Task<ActionDto> LoadReplicaAsync(string serviceDirectory, string service, string publisher, string entity, CancellationToken cancellationToken)
    {
        var url = BaseUrl(serviceDirectory, service);
        try
        {
            using var response = await LoadHttp.PostAsync(new Uri($"{url}/_stratum/replicas/{Uri.EscapeDataString(publisher)}/{Uri.EscapeDataString(entity)}/load"), null, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new(false, I18n.L(
                    $"{service} no tiene la carga inicial: aplica los cambios (Stratum 0.44 o posterior) y arráncalo en Development.",
                    $"{service} doesn't have the initial load: apply the changes (Stratum 0.44 or later) and run it in Development."), null);
            }

            var result = await response.Content.ReadFromJsonAsync<ReplicaLoadDto>(Json, cancellationToken).ConfigureAwait(false);
            return result is null ? new(false, $"HTTP {(int)response.StatusCode}", null)
                : result.Problem is { } problem ? new(false, problem, null)
                : new(true, I18n.L(
                    $"{result.Read} leídos de {publisher}: {result.Written} escritos, {result.Removed} quitados (ya no estaban).",
                    $"{result.Read} read from {publisher}: {result.Written} written, {result.Removed} removed (no longer there)."), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(false, I18n.L($"{service} no está arrancado (nada responde en {url}).", $"{service} isn't running (nothing answers on {url})."), null);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return new(false, e.Message, null);
        }
    }

    private sealed record ReplicaLoadDto(string Replica, int Read, int Written, int Removed, string? Problem);

    /// <summary>POST to a running service of the system (its Development diagnostics).</summary>
    private static async Task<ActionDto> CallAsync(string workspace, string service, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        var found = AspireGenerator.Discover(workspace).FirstOrDefault(s => s.Definition.Service.Name == service);
        if (found is null)
        {
            return new(false, I18n.L($"No encuentro el servicio {service} en el sistema.", $"Service {service} isn't in the system."), null);
        }

        var url = BaseUrl(Path.GetFullPath(Path.Combine(workspace, found.RelativeDirectory)), found.Definition);
        try
        {
            using var content = body is null ? null : JsonContent.Create(body);
            using var response = await Http.PostAsync(new Uri(url + path), content, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new(false, I18n.L(
                    $"{service} no tiene esta acción: actualiza sus paquetes a Stratum 0.38 (o posterior) y arráncalo en Development.",
                    $"{service} doesn't have this action: upgrade its packages to Stratum 0.38 (or later) and run it in Development."), null);
            }

            var outcome = await response.Content.ReadFromJsonAsync<ActionDto>(Json, cancellationToken).ConfigureAwait(false);
            return outcome ?? new(false, $"HTTP {(int)response.StatusCode}", null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(false, I18n.L($"{service} no está arrancado (nada responde en {url}).", $"{service} isn't running (nothing answers on {url})."), null);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return new(false, e.Message, null);
        }
    }

    private static async Task<ReportDto> ReportAsync(string workspace, int minutes, CancellationToken cancellationToken)
    {
        var services = AspireGenerator.Discover(workspace);
        var routes = EventMap.Build(services.Select(s => s.Definition));
        var withMessaging = services.Where(s => s.Definition.Messaging is not null).ToList();
        var probes = await Task.WhenAll(withMessaging.Select(s =>
            ProbeAsync(s.Definition.Service.Name, s.RelativeDirectory, BaseUrl(Path.GetFullPath(Path.Combine(workspace, s.RelativeDirectory)), s.Definition), minutes, cancellationToken))).ConfigureAwait(false);

        var report = EventTraces.Build(routes, probes, DateTime.UtcNow);
        return new ReportDto(
            AspireGenerator.DefaultSystemName(workspace),
            report.GeneratedAt,
            [.. report.Services.Select(p => new ServiceDto(p.Name, p.Directory, p.Url, p.State, p.Message, p.Snapshot?.Broker, p.Snapshot?.Problems ?? [],
                Publishable(withMessaging.FirstOrDefault(s => s.Definition.Service.Name == p.Name)?.Definition)))],
            report.Events,
            Links(workspace, withMessaging.Any(s => s.Definition.Messaging?.Broker == MessagingSettings.Kafka)));
    }

    private static async Task<ServiceProbe> ProbeAsync(string name, string directory, string url, int minutes, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(new Uri($"{url}/_stratum/messaging?minutes={minutes}&max=300"), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new(name, directory, url, ProbeState.NoEndpoint, I18n.L(
                    "Responde, pero sin /_stratum/messaging: actualiza sus paquetes a Stratum 0.35 (o posterior) y arráncalo en Development.",
                    "It answers, but without /_stratum/messaging: upgrade its packages to Stratum 0.35 (or later) and run it in Development."), null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(name, directory, url, ProbeState.Error, $"HTTP {(int)response.StatusCode}", null);
            }

            var snapshot = await response.Content.ReadFromJsonAsync<ServiceSnapshot>(Json, cancellationToken).ConfigureAwait(false);
            return new(name, directory, url, ProbeState.Ok, null, snapshot);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(name, directory, url, ProbeState.Offline, I18n.L("No está arrancado (nada responde en ese puerto).", "Not running (nothing answers on that port)."), null);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return new(name, directory, url, ProbeState.Error, e.Message, null);
        }
    }

    private static IReadOnlyList<PublishableDto> Publishable(ServiceDefinition? definition) =>
        definition is null ? [] : [.. IntegrationEvents.Of(definition).Select(e => new PublishableDto(e.Topic, e.EventClass, EntityCodeGen.EventSample(definition, e)))];

    /// <summary>The service's http address in Development: its launchSettings.json (what Aspire uses too), else the port Stratum gave it.</summary>
    private static string BaseUrl(string directory, ServiceDefinition definition) => BaseUrl(directory, definition.Service.Name);

    private static string BaseUrl(string directory, string name)
    {
        var launch = Path.Combine(directory, "src", $"{name}.Api", "Properties", "launchSettings.json");
        try
        {
            if (File.Exists(launch) && JsonNode.Parse(File.ReadAllText(launch))?["profiles"] is JsonObject profiles)
            {
                foreach (var (_, profile) in profiles)
                {
                    var urls = profile?["applicationUrl"]?.GetValue<string>()?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
                    if (urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) is { } http)
                    {
                        return http.TrimEnd('/');
                    }
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
        {
            // Unreadable launchSettings: the default port.
        }

        return $"http://localhost:{ServiceName.DevPort(name)}";
    }

    /// <summary>Aspire dashboard and Kafka UI of the system's AppHost (ports fixed by "stratum aspire").</summary>
    private static LinksDto Links(string workspace, bool kafka)
    {
        var appHost = Directory.Exists(workspace)
            ? Directory.EnumerateDirectories(workspace, "*.AppHost").Select(Path.GetFileName).FirstOrDefault()
            : null;
        if (appHost is null)
        {
            return new(null, null);
        }

        var name = appHost[..^".AppHost".Length];
        string? dashboard = null;
        var launch = Path.Combine(workspace, appHost, "Properties", "launchSettings.json");
        try
        {
            dashboard = JsonNode.Parse(File.ReadAllText(launch))?["profiles"]?["http"]?["applicationUrl"]?.GetValue<string>()?.Split(';')[0].TrimEnd('/');
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // No launchSettings: the port Stratum would have given it.
        }

        dashboard ??= $"http://localhost:{AspireGenerator.DashboardPort(name)}";
        return new(dashboard, kafka ? $"http://localhost:{AspireGenerator.KafkaUiPort(name)}" : null);
    }

    /// <summary>Dates from the services are UTC; some come without the "Z".</summary>
    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetDateTime();
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            };
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}
