namespace Stratum.Messaging;

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

/// <summary>
/// What happened to a received message in one handler of this service: the journal behind Stratum Studio's
/// "Event traces". One row per (message, handler), updated on each attempt. Unlike the inbox it also keeps failures.
/// </summary>
public sealed class ReceivedMessage
{
    public required string MessageId { get; set; }

    /// <summary>Handler (same as <see cref="InboxMessage.Consumer"/>).</summary>
    public required string Consumer { get; set; }

    public required string Topic { get; set; }

    /// <summary>Service that published it.</summary>
    public required string Source { get; set; }

    public string? CorrelationId { get; set; }

    /// <summary>W3C trace id (32 hex characters): the same trace as the request that published the event.</summary>
    public string? TraceId { get; set; }

    /// <summary>When it first arrived (UTC).</summary>
    public DateTime ReceivedAt { get; set; }

    /// <summary>When the last attempt finished (UTC).</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>One of <see cref="ReceivedStatus"/>.</summary>
    public required string Status { get; set; }

    public int Attempts { get; set; }

    /// <summary>Duration of the last attempt.</summary>
    public int DurationMs { get; set; }

    public string? LastError { get; set; }
}

/// <summary>Values of <see cref="ReceivedMessage.Status"/>.</summary>
public static class ReceivedStatus
{
    /// <summary>The handler finished.</summary>
    public const string Processed = "processed";

    /// <summary>An attempt failed; another one is coming.</summary>
    public const string Retrying = "retrying";

    /// <summary>Every attempt failed: moved to the dead-letter queue/topic.</summary>
    public const string DeadLettered = "deadLettered";

    /// <summary>The payload isn't the expected event: moved to the dead-letter queue/topic without running the handler.</summary>
    public const string Unreadable = "unreadable";
}

/// <summary>
/// Storage of the journal and read access to the outbox for diagnostics (scoped). Implemented in memory and by
/// Stratum.Messaging.EntityFrameworkCore.
/// </summary>
public interface IMessageJournal
{
    /// <summary>Adds or updates the row of (MessageId, Consumer).</summary>
    Task RecordAsync(ReceivedMessage entry, CancellationToken cancellationToken);

    /// <summary>Outbox messages that occurred since <paramref name="sinceUtc"/>, newest first.</summary>
    Task<IReadOnlyList<OutboxMessage>> SentSinceAsync(DateTime sinceUtc, int max, CancellationToken cancellationToken);

    /// <summary>Journal rows updated since <paramref name="sinceUtc"/>, newest first.</summary>
    Task<IReadOnlyList<ReceivedMessage>> ReceivedSinceAsync(DateTime sinceUtc, int max, CancellationToken cancellationToken);

    /// <summary>Deletes journal rows and sent outbox messages older than <paramref name="beforeUtc"/> (pending ones are kept).</summary>
    Task<int> PurgeAsync(DateTime beforeUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Puts an outbox message back in the queue (sent or given up), as it was: the dispatcher sends it again with the
    /// same id, so the handlers that already processed it skip it (inbox) and the ones that failed run again.
    /// </summary>
    /// <returns>False when the outbox no longer has it (purged after the trace retention).</returns>
    Task<bool> ResendAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Adds a message to the outbox and saves it now (test events of Stratum Studio).</summary>
    Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken);
}
