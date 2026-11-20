using System.Diagnostics;

namespace Stratum.Messaging;

/// <summary>Traces of publishing and consuming. Add the source to OpenTelemetry (Stratum.ServiceDefaults.OpenTelemetry does).</summary>
public static class MessagingTelemetry
{
    public const string SourceName = "Stratum.Messaging";

    internal static readonly ActivitySource Source = new(SourceName);

    internal static Activity? StartSend(OutboxMessage message)
    {
        ActivityContext.TryParse(message.TraceParent, null, out var parent);
        var activity = Source.StartActivity($"{message.Topic} publish", ActivityKind.Producer, parent);
        activity?.SetTag("messaging.destination.name", message.Topic);
        activity?.SetTag("messaging.message.id", message.Id.ToString());
        activity?.SetTag("messaging.operation.type", "send");
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
        return activity;
    }
}
