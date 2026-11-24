using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Stratum.Messaging;

/// <summary>
/// Traces and metrics of publishing and consuming. Add the source and the meter to OpenTelemetry
/// (Stratum.ServiceDefaults.OpenTelemetry does): the Aspire dashboard then shows one trace from the request that
/// published the event to every service that handled it.
/// </summary>
public static class MessagingTelemetry
{
    public const string SourceName = "Stratum.Messaging";

    /// <summary>Name of the meter (same as the activity source).</summary>
    public const string MeterName = SourceName;

    internal static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> SentCounter = Meter.CreateCounter<long>(
        "stratum.messaging.sent", "{message}", "Integration events sent to the broker (outcome: sent, failed).");

    private static readonly Counter<long> ProcessedCounter = Meter.CreateCounter<long>(
        "stratum.messaging.processed", "{message}", "Received integration events by handler (outcome: processed, retrying, deadLettered, unreadable).");

    private static readonly Histogram<double> ProcessDuration = Meter.CreateHistogram<double>(
        "stratum.messaging.process.duration", "ms", "Time a handler took with one received event.");

    /// <summary>The trace id (32 hex characters) of a W3C traceparent ("00-traceid-spanid-flags"), or null.</summary>
    public static string? TraceIdOf(string? traceParent) =>
        traceParent is { Length: >= 35 } && traceParent[2] == '-' && traceParent[35] == '-' ? traceParent[3..35] : null;

    internal static Activity? StartSend(OutboxMessage message, string? broker = null)
    {
        ActivityContext.TryParse(message.TraceParent, null, out var parent);
        var activity = Source.StartActivity($"{message.Topic} publish", ActivityKind.Producer, parent);
        activity?.SetTag("messaging.destination.name", message.Topic);
        activity?.SetTag("messaging.message.id", message.Id.ToString());
        activity?.SetTag("messaging.operation.type", "send");
        activity?.SetTag("messaging.system", broker);
        activity?.SetTag("stratum.messaging.event_type", message.Type);
        activity?.SetTag("stratum.messaging.correlation_id", message.CorrelationId);
        return activity;
    }

    internal static Activity? StartProcess(MessageEnvelope envelope, string consumer)
    {
        ActivityContext.TryParse(envelope.TraceParent, null, out var parent);
        var activity = Source.StartActivity($"{envelope.Topic} process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.destination.name", envelope.Topic);
        activity?.SetTag("messaging.message.id", envelope.MessageId);
        activity?.SetTag("messaging.consumer.group.name", consumer);
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("stratum.messaging.source", envelope.Source);
        activity?.SetTag("stratum.messaging.correlation_id", envelope.CorrelationId);
        return activity;
    }

    internal static void Sent(string topic, bool ok) =>
        SentCounter.Add(1, new KeyValuePair<string, object?>("messaging.destination.name", topic), new KeyValuePair<string, object?>("outcome", ok ? "sent" : "failed"));

    internal static void Processed(string topic, string consumer, string outcome, int durationMs)
    {
        var tags = new TagList
        {
            { "messaging.destination.name", topic },
            { "messaging.consumer.group.name", consumer },
            { "outcome", outcome },
        };
        ProcessedCounter.Add(1, tags);
        ProcessDuration.Record(durationMs, tags);
    }
}
