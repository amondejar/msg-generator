namespace Msg.Messaging;

/// <summary>What travels through the broker: the event as JSON plus the metadata to route, deduplicate and trace it.</summary>
public sealed record MessageEnvelope
{
    /// <summary>The event's EventId. Consumers deduplicate by it.</summary>
    public required string MessageId { get; init; }

    public required string Topic { get; init; }

    /// <summary>Service that published it (e.g. "orders").</summary>
    public required string Source { get; init; }

    /// <summary>JSON (System.Text.Json, web defaults).</summary>
    public required string Payload { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>W3C traceparent of the publishing request: the consumer's trace continues it.</summary>
    public string? TraceParent { get; init; }
}

/// <summary>Outcome of handling a message, for the transport to acknowledge it.</summary>
public enum ConsumeResult
{
    /// <summary>Handled (or already handled before): remove it from the broker.</summary>
    Completed,

    /// <summary>Failed every attempt or can't be read: move it to the dead-letter queue/topic.</summary>
    DeadLetter,
}

/// <summary>
/// A message broker. Implemented by Msg.Messaging.Kafka, Msg.Messaging.AzureServiceBus and the in-memory transport.
/// </summary>
public interface IMessageTransport
{
    Task SendAsync(MessageEnvelope envelope, CancellationToken cancellationToken);

    /// <summary>
    /// Receives the messages of <paramref name="topics"/> as <paramref name="subscription"/> (consumer group /
    /// subscription name) until <paramref name="cancellationToken"/> is cancelled. Each subscription gets every message once.
    /// </summary>
    Task ConsumeAsync(
        IReadOnlyCollection<string> topics,
        string subscription,
        Func<MessageEnvelope, CancellationToken, Task<ConsumeResult>> onMessage,
        CancellationToken cancellationToken);
}
