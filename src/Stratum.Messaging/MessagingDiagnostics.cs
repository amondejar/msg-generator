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
    /// Event traces). It shows payloads: map it only in Development.
    /// </summary>
    public static RouteHandlerBuilder MapStratumMessagingDiagnostics(this IEndpointRouteBuilder endpoints, string path = Path)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGet(path, async (MessagingDiagnostics diagnostics, int? minutes, int? max, CancellationToken cancellationToken) =>
            Results.Ok(await diagnostics.SnapshotAsync(
                TimeSpan.FromMinutes(Math.Clamp(minutes ?? 60, 1, 60 * 24 * 7)),
                Math.Clamp(max ?? 200, 1, 1000),
                cancellationToken).ConfigureAwait(false)))
            .ExcludeFromDescription();
    }
}
