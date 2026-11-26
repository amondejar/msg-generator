using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Stratum.Messaging;

/// <summary>What one service has sent and received lately: the data behind Stratum Studio's "Event traces".</summary>
public sealed record MessagingSnapshot(
    string Service,
    string? Broker,
    DateTime GeneratedAt,
    IReadOnlyList<HandledTopic> Handles,
    IReadOnlyList<SentMessage> Sent,
    IReadOnlyList<ReceivedMessage> Received,
    IReadOnlyList<string> Problems);

/// <summary>Result of an action of Stratum Studio's event traces (resend, test event).</summary>
public sealed record ActionOutcome(bool Ok, string Message, string? MessageId = null)
{
    public static ActionOutcome Done(string message, string? messageId = null) => new(true, message, messageId);

    public static ActionOutcome Fail(string message) => new(false, message);
}

/// <summary>Body of POST /_stratum/messaging/publish.</summary>
public sealed record TestEventRequest(string Topic, JsonObject? Payload);

/// <summary>A topic this service listens to, and the handler that runs.</summary>
public sealed record HandledTopic(string Topic, string Consumer, string Event);

/// <summary>An outbox message as the diagnostics show it.</summary>
public sealed record SentMessage(
    string MessageId,
    string Topic,
    string Type,
    DateTime OccurredAt,
    DateTime? SentAt,
    string Status,
    int Attempts,
    DateTime? NextAttemptAt,
    string? LastError,
    string? CorrelationId,
    string? TraceId,
    string Payload);

/// <summary>Values of <see cref="SentMessage.Status"/>.</summary>
public static class SentStatus
{
    /// <summary>In the outbox, not sent yet.</summary>
    public const string Pending = "pending";

    /// <summary>The broker rejected it (or wasn't reachable); it will be retried.</summary>
    public const string Retrying = "retrying";

    /// <summary>Gave up after MessagingOptions.OutboxMaxAttempts: see LastError.</summary>
    public const string Failed = "failed";

    /// <summary>The broker accepted it.</summary>
    public const string Sent = "sent";
}

/// <summary>Builds <see cref="MessagingSnapshot"/>s (singleton).</summary>
public sealed class MessagingDiagnostics(IServiceScopeFactory scopes, IServiceProvider services, HandlerRegistry registry, IOptions<MessagingOptions> options, TimeProvider time)
{
    private const int MaxPayload = 8000;

    public async Task<MessagingSnapshot> SnapshotAsync(TimeSpan window, int max, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var since = now - window;
        var problems = new List<string>();
        var transport = services.GetService<IMessageTransport>();
        if (transport is null)
        {
            problems.Add("No broker configured (UseKafka, UseAzureServiceBus or UseInMemory).");
        }

        IReadOnlyList<SentMessage> sent = [];
        IReadOnlyList<ReceivedMessage> received = [];
        await using var scope = scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IMessageJournal>() is { } journal)
        {
            try
            {
                sent = [.. (await journal.SentSinceAsync(since, max, cancellationToken).ConfigureAwait(false)).Select(Describe)];
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                problems.Add($"Outbox (msg_outbox): {ex.GetBaseException().Message}");
            }

            try
            {
                received = [.. (await journal.ReceivedSinceAsync(since, max, cancellationToken).ConfigureAwait(false)).Select(Utc)];
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                problems.Add($"Journal (msg_received): {ex.GetBaseException().Message}. Create a migration: dotnet ef migrations add StratumMessagingJournal");
            }
        }

        if (!options.Value.JournalEnabled && registry.Handlers.Count > 0)
        {
            problems.Add("Messaging:JournalEnabled is false: received messages aren't recorded.");
        }

        return new MessagingSnapshot(
            options.Value.ServiceName,
            transport is null ? null : MessagingServices.BrokerOf(transport),
            now,
            [.. registry.Handlers.Select(h => new HandledTopic(h.Topic, h.Consumer, h.EventType.Name))],
            sent,
            received,
            problems);
    }

    /// <summary>Sends an outbox message again (see <see cref="IMessageJournal.ResendAsync"/>).</summary>
    public async Task<ActionOutcome> ResendAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IMessageJournal>() is not { } journal)
        {
            return ActionOutcome.Fail("This service has no message journal (AddStratumMessaging with a store).");
        }

        return await journal.ResendAsync(id, cancellationToken).ConfigureAwait(false)
            ? ActionOutcome.Done($"{id} is in the outbox again: it will be sent in a moment. Handlers that already processed it skip it.")
            : ActionOutcome.Fail($"The outbox of {options.Value.ServiceName} no longer has {id} (sent messages are kept {options.Value.TraceRetention.TotalDays:0.#} days).");
    }

    /// <summary>
    /// Publishes a test event: <paramref name="payload"/> must be the JSON of one of this service's integration events
    /// (the one of <paramref name="topic"/>). It gets a new EventId and OccurredAt and goes through the outbox like
    /// any other, so consumers receive it as a real one.
    /// </summary>
    public async Task<ActionOutcome> PublishTestAsync(string topic, JsonObject? payload, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        var types = EventTypes().Where(t => EventTopology.TopicOf(t) == topic).ToList();
        var handled = registry.Handlers.Select(h => h.EventType).ToHashSet();
        var type = types.FirstOrDefault(t => !handled.Contains(t)) ?? types.FirstOrDefault();
        if (type is null)
        {
            return ActionOutcome.Fail($"{options.Value.ServiceName} has no integration event with the topic '{topic}'.");
        }

        var json = payload ?? [];
        json["eventId"] = Guid.CreateVersion7();
        json["occurredAt"] = time.GetUtcNow();
        IIntegrationEvent? integrationEvent;
        try
        {
            integrationEvent = json.Deserialize(type, EventTopology.JsonOptions) as IIntegrationEvent;
        }
        catch (JsonException ex)
        {
            return ActionOutcome.Fail($"The data isn't a valid {type.Name}: {ex.Message}");
        }

        if (integrationEvent is null)
        {
            return ActionOutcome.Fail($"The data isn't a valid {type.Name}.");
        }

        await using var scope = scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IMessageJournal>() is not { } journal)
        {
            return ActionOutcome.Fail("This service has no message journal (AddStratumMessaging with a store).");
        }

        var message = OutboxMessages.From(integrationEvent, type);
        await journal.EnqueueAsync(message, cancellationToken).ConfigureAwait(false);
        return ActionOutcome.Done($"{type.Name} {message.Id} is in the outbox: it will be sent in a moment.", message.Id.ToString());
    }

    private static IReadOnlyList<Type>? _eventTypes;

    /// <summary>The integration event classes of the application (its own assemblies, not the framework's).</summary>
    private static IReadOnlyList<Type> EventTypes()
    {
        if (_eventTypes is { } cached)
        {
            return cached;
        }

        var assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        void Add(Assembly assembly, int depth)
        {
            var name = assembly.GetName().Name ?? string.Empty;
            if (depth > 3 || assemblies.ContainsKey(name) || IsFramework(name))
            {
                return;
            }

            assemblies[name] = assembly;
            foreach (var reference in assembly.GetReferencedAssemblies().Where(r => !IsFramework(r.Name ?? string.Empty)))
            {
                try
                {
                    Add(Assembly.Load(reference), depth + 1);
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // Not loadable here: it can't hold this service's events.
                }
            }
        }

        if (Assembly.GetEntryAssembly() is { } entry)
        {
            Add(entry, 0);
        }

        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            Add(loaded, 3);
        }

        _eventTypes = [.. assemblies.Values.SelectMany(Types).Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IIntegrationEvent).IsAssignableFrom(t))];
        return _eventTypes;
    }

    private static bool IsFramework(string name) =>
        name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft", StringComparison.Ordinal)
        || name.StartsWith("Stratum.Messaging", StringComparison.Ordinal) || name is "netstandard" or "mscorlib";

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    /// <summary>Databases give the dates back without a kind: they are UTC (so the JSON says "Z").</summary>
    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    private static ReceivedMessage Utc(ReceivedMessage m) => new()
    {
        MessageId = m.MessageId,
        Consumer = m.Consumer,
        Topic = m.Topic,
        Source = m.Source,
        CorrelationId = m.CorrelationId,
        TraceId = m.TraceId,
        ReceivedAt = Utc(m.ReceivedAt),
        UpdatedAt = Utc(m.UpdatedAt),
        Status = m.Status,
        Attempts = m.Attempts,
        DurationMs = m.DurationMs,
        LastError = m.LastError,
    };

    private static SentMessage Describe(OutboxMessage m) => new(
        m.Id.ToString(),
        m.Topic,
        m.Type,
        Utc(m.OccurredAt),
        Utc(m.SentAt),
        m.SentAt is not null ? SentStatus.Sent
            : m.NextAttemptAt == OutboxDispatcher.GivenUp ? SentStatus.Failed
            : m.Attempts > 0 ? SentStatus.Retrying
            : SentStatus.Pending,
        m.Attempts,
        m.NextAttemptAt == OutboxDispatcher.GivenUp ? null : Utc(m.NextAttemptAt),
        m.LastError,
        m.CorrelationId,
        MessagingTelemetry.TraceIdOf(m.TraceParent),
        m.Payload.Length > MaxPayload ? m.Payload[..MaxPayload] + "…" : m.Payload);
}

public static class MessagingDiagnosticsEndpoints
{
    /// <summary>Default path of <see cref="MapStratumMessagingDiagnostics"/>.</summary>
    public const string Path = "/_stratum/messaging";

    /// <summary>
    /// GET /_stratum/messaging?minutes=60&amp;max=200: what this service sent and received lately (Stratum Studio →
    /// Event traces). Also POST {path}/resend/{id} (send an outbox message again) and POST {path}/publish (a test event).
    /// It shows payloads and publishes events: map it only in Development.
    /// </summary>
    public static RouteGroupBuilder MapStratumMessagingDiagnostics(this IEndpointRouteBuilder endpoints, string path = Path)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup(path).ExcludeFromDescription();
        group.MapGet(string.Empty, async (MessagingDiagnostics diagnostics, int? minutes, int? max, CancellationToken cancellationToken) =>
            Results.Ok(await diagnostics.SnapshotAsync(
                TimeSpan.FromMinutes(Math.Clamp(minutes ?? 60, 1, 60 * 24 * 7)),
                Math.Clamp(max ?? 200, 1, 1000),
                cancellationToken).ConfigureAwait(false)));
        group.MapPost("/resend/{id:guid}", async (MessagingDiagnostics diagnostics, Guid id, CancellationToken cancellationToken) =>
            Outcome(await diagnostics.ResendAsync(id, cancellationToken).ConfigureAwait(false)));
        group.MapPost("/publish", async (MessagingDiagnostics diagnostics, TestEventRequest request, CancellationToken cancellationToken) =>
            string.IsNullOrWhiteSpace(request?.Topic)
                ? Results.BadRequest(ActionOutcome.Fail("Missing topic."))
                : Outcome(await diagnostics.PublishTestAsync(request.Topic, request.Payload, cancellationToken).ConfigureAwait(false)));
        return group;
    }

    private static IResult Outcome(ActionOutcome outcome) => outcome.Ok ? Results.Ok(outcome) : Results.UnprocessableEntity(outcome);
}
