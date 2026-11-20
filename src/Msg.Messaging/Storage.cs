namespace Msg.Messaging;

/// <summary>
/// An integration event waiting to be sent (or already sent) to the broker. Dates are UTC DateTime (not
/// DateTimeOffset) so every provider, SQLite included, can sort and compare them.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    public required string Topic { get; set; }

    public required string Type { get; set; }

    public required string Payload { get; set; }

    public DateTime OccurredAt { get; set; }

    public string? CorrelationId { get; set; }

    public string? TraceParent { get; set; }

    /// <summary>Null while pending.</summary>
    public DateTime? SentAt { get; set; }

    public int Attempts { get; set; }

    public DateTime? NextAttemptAt { get; set; }

    public string? LastError { get; set; }
}

/// <summary>A message already handled by a consumer (idempotency).</summary>
public sealed class InboxMessage
{
    public required string MessageId { get; set; }

    /// <summary>Handler that processed it: one event can be handled by several handlers of the same service.</summary>
    public required string Consumer { get; set; }

    public DateTime ProcessedAt { get; set; }
}

/// <summary>Adds messages to the outbox in the current unit of work (scoped).</summary>
public interface IOutboxWriter
{
    void Add(OutboxMessage message);
}

/// <summary>Reads and updates the outbox for the dispatcher (scoped).</summary>
public interface IOutboxStore
{
    Task<IReadOnlyList<OutboxMessage>> FetchPendingAsync(int max, DateTime utcNow, CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>Runs a handler at most once per (message, consumer) (scoped).</summary>
public interface IInbox
{
    /// <returns>False when the message was already processed by that consumer (nothing runs).</returns>
    Task<bool> ExecuteOnceAsync(string messageId, string consumer, Func<CancellationToken, Task> action, CancellationToken cancellationToken);
}
