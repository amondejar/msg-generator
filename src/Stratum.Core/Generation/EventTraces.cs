using Stratum.Core.Model;

namespace Stratum.Core.Generation;

// What each running service reports on GET /_stratum/messaging (Stratum.Messaging.MessagingSnapshot), as JSON.

/// <summary>Snapshot of one service: what it sent (outbox) and what its handlers received (msg_received).</summary>
public sealed record ServiceSnapshot(
    string Service,
    string? Broker,
    DateTime GeneratedAt,
    IReadOnlyList<SnapshotHandler> Handles,
    IReadOnlyList<SnapshotSent> Sent,
    IReadOnlyList<SnapshotReceived> Received,
    IReadOnlyList<string> Problems);

public sealed record SnapshotHandler(string Topic, string Consumer, string Event);

public sealed record SnapshotSent(
    string MessageId, string Topic, string Type, DateTime OccurredAt, DateTime? SentAt, string Status, int Attempts,
    DateTime? NextAttemptAt, string? LastError, string? CorrelationId, string? TraceId, string Payload);

public sealed record SnapshotReceived(
    string MessageId, string Consumer, string Topic, string Source, string? CorrelationId, string? TraceId, DateTime ReceivedAt,
    DateTime UpdatedAt, string Status, int Attempts, int DurationMs, string? LastError);

/// <summary>How Stratum Studio reached a service.</summary>
public static class ProbeState
{
    /// <summary>Running, and its snapshot was read.</summary>
    public const string Ok = "ok";

    /// <summary>Nothing answers on its port: not running (or on another port).</summary>
    public const string Offline = "offline";

    /// <summary>It answers but has no /_stratum/messaging: older Stratum packages, or not in Development.</summary>
    public const string NoEndpoint = "noEndpoint";

    public const string Error = "error";
}

/// <summary>A service of the system with messaging, and what came back when asking it.</summary>
public sealed record ServiceProbe(string Name, string Directory, string Url, string State, string? Message, ServiceSnapshot? Snapshot);

/// <summary>What happened to an event in one service that listens to it.</summary>
public sealed record EventDelivery(
    string Service,
    string? Handler,
    string Status,
    DateTime? ReceivedAt,
    DateTime? UpdatedAt,
    int Attempts,
    int DurationMs,
    string? LastError,
    int? LatencyMs);

/// <summary>Values of <see cref="EventDelivery.Status"/> (the first four come from the service's journal).</summary>
public static class DeliveryStatus
{
    public const string Processed = "processed";
    public const string Retrying = "retrying";
    public const string DeadLettered = "deadLettered";
    public const string Unreadable = "unreadable";

    /// <summary>Not received yet, but it's early (or the event hasn't reached the broker).</summary>
    public const string Waiting = "waiting";

    /// <summary>The broker accepted it a while ago and the listening service, running, hasn't received it.</summary>
    public const string Missing = "missing";

    /// <summary>The listening service isn't running (or Stratum Studio can't read it).</summary>
    public const string Unknown = "unknown";

    /// <summary>stratum.json says it listens, but the running service has no handler for the topic (not rebuilt?).</summary>
    public const string NotSubscribed = "notSubscribed";
}

/// <summary>One integration event, from the publisher's outbox to every service that listens to it.</summary>
public sealed record EventTrace(
    string MessageId,
    string Topic,
    string Event,
    string? Publisher,
    DateTime At,
    string? CorrelationId,
    string? TraceId,
    string? Payload,
    SnapshotSent? Sent,
    IReadOnlyList<EventDelivery> Deliveries,
    string Outcome);

/// <summary>Values of <see cref="EventTrace.Outcome"/>, from worst to best.</summary>
public static class TraceOutcome
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Waiting = "waiting";
    public const string Ok = "ok";

    /// <summary>Sent, and no service listens to it.</summary>
    public const string NoListeners = "noListeners";
}

public sealed record TraceReport(DateTime GeneratedAt, IReadOnlyList<ServiceProbe> Services, IReadOnlyList<EventTrace> Events);

/// <summary>
/// Joins what the services of a system report (outbox of the publisher, journal of each listener) by message id,
/// and compares it with the event map of the stratum.json files to say what hasn't arrived where it should.
/// </summary>
public static class EventTraces
{
    /// <summary>After this long accepted by the broker, an event not received by a running listener is "missing".</summary>
    public static readonly TimeSpan MissingAfter = TimeSpan.FromSeconds(30);

    public static TraceReport Build(IReadOnlyList<EventRoute> routes, IReadOnlyList<ServiceProbe> services, DateTime nowUtc, int max = 300)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(services);

        var routesByTopic = routes.ToDictionary(r => r.Topic, StringComparer.Ordinal);
        var byName = services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        // Every message seen, by id: the publisher's outbox row and the journal rows of each listener.
        var sent = new Dictionary<string, (string Publisher, SnapshotSent Row)>(StringComparer.Ordinal);
        var received = new Dictionary<string, List<(string Service, SnapshotReceived Row)>>(StringComparer.Ordinal);
        foreach (var service in services.Where(s => s.Snapshot is not null))
        {
            foreach (var row in service.Snapshot!.Sent)
            {
                sent[row.MessageId] = (service.Name, row);
            }

            foreach (var row in service.Snapshot.Received)
            {
                if (!received.TryGetValue(row.MessageId, out var list))
                {
                    received[row.MessageId] = list = [];
                }

                list.Add((service.Name, row));
            }
        }

        var events = new List<EventTrace>();
        foreach (var id in sent.Keys.Union(received.Keys, StringComparer.Ordinal))
        {
            var (publisher, outbox) = sent.TryGetValue(id, out var s) ? s : (null, null);
            var rows = received.GetValueOrDefault(id) ?? [];
            var first = rows.Count > 0 ? rows[0].Row : null;
            var topic = outbox?.Topic ?? first!.Topic;
            routesByTopic.TryGetValue(topic, out var route);
            publisher ??= ServiceOf(first!.Source, services) ?? route?.Publisher ?? first!.Source;

            var deliveries = rows
                .Select(r => new EventDelivery(
                    r.Service, ShortName(r.Row.Consumer), r.Row.Status, r.Row.ReceivedAt, r.Row.UpdatedAt, r.Row.Attempts, r.Row.DurationMs, r.Row.LastError,
                    outbox?.SentAt is { } at ? Math.Max(0, (int)(r.Row.ReceivedAt - at).TotalMilliseconds) : null))
                .ToList();

            // Services that should have it (event map) and have no row. (Not for an event the outbox gave up on: it won't arrive.)
            foreach (var consumer in outbox?.Status == "failed" ? [] : route?.Consumers ?? [])
            {
                if (deliveries.Any(d => string.Equals(d.Service, consumer, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                deliveries.Add(new EventDelivery(consumer, null, Expected(consumer, topic, outbox, byName, nowUtc), null, null, 0, 0, null, null));
            }

            var eventName = route?.EventClass ?? ShortName(outbox?.Type ?? topic);
            events.Add(new EventTrace(
                id,
                topic,
                eventName,
                publisher,
                outbox?.OccurredAt ?? rows.Min(r => r.Row.ReceivedAt),
                outbox?.CorrelationId ?? first?.CorrelationId,
                outbox?.TraceId ?? first?.TraceId,
                outbox?.Payload,
                outbox,
                [.. deliveries.OrderBy(d => d.Service, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Handler, StringComparer.Ordinal)],
                Outcome(outbox, deliveries)));
        }

        return new TraceReport(nowUtc, services, [.. events.OrderByDescending(e => e.At).Take(max)]);
    }

    private static string Expected(string consumer, string topic, SnapshotSent? outbox, Dictionary<string, ServiceProbe> services, DateTime nowUtc)
    {
        // Not running, or its journal can't be read (msg_received not migrated yet…): nothing to say.
        if (!services.TryGetValue(consumer, out var probe) || probe.Snapshot is not { } snapshot || snapshot.Problems.Count > 0)
        {
            return DeliveryStatus.Unknown;
        }

        if (!snapshot.Handles.Any(h => h.Topic == topic))
        {
            return DeliveryStatus.NotSubscribed;
        }

        return outbox?.SentAt is { } sentAt && nowUtc - sentAt > MissingAfter ? DeliveryStatus.Missing : DeliveryStatus.Waiting;
    }

    private static string Outcome(SnapshotSent? outbox, List<EventDelivery> deliveries)
    {
        var statuses = deliveries.Select(d => d.Status).ToList();
        if (outbox?.Status == "failed" || statuses.Any(s => s is DeliveryStatus.DeadLettered or DeliveryStatus.Unreadable))
        {
            return TraceOutcome.Error;
        }

        if (outbox?.Status == "retrying" || statuses.Any(s => s is DeliveryStatus.Retrying or DeliveryStatus.Missing or DeliveryStatus.NotSubscribed))
        {
            return TraceOutcome.Warning;
        }

        // A listener that isn't running says nothing either way: only if no listener is known is the event still "waiting".
        if (outbox?.Status == "pending" || statuses.Any(s => s is DeliveryStatus.Waiting)
            || (statuses.Count > 0 && statuses.All(s => s is DeliveryStatus.Unknown)))
        {
            return TraceOutcome.Waiting;
        }

        return deliveries.Count == 0 ? TraceOutcome.NoListeners : TraceOutcome.Ok;
    }

    /// <summary>The service of a message's source ("orders", the kebab-case name messaging uses).</summary>
    private static string? ServiceOf(string source, IReadOnlyList<ServiceProbe> services) =>
        services.FirstOrDefault(s => string.Equals(ServiceName.ToKebab(s.Name), source, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Snapshot?.Service, source, StringComparison.OrdinalIgnoreCase))?.Name;

    /// <summary>"Billing.Api.IntegrationEvents.OrderCreatedHandler" → "OrderCreatedHandler".</summary>
    public static string ShortName(string typeName)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        var name = typeName.Split(',')[0];
        var dot = name.LastIndexOfAny(['.', '+']);
        return dot >= 0 ? name[(dot + 1)..] : name;
    }
}
