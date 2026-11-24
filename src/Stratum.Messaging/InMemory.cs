using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Stratum.Messaging;

/// <summary>
/// Broker inside the process: for tests and for running one service alone. Messages don't survive a restart
/// and don't reach other processes; use Kafka or Azure Service Bus between services.
/// </summary>
public sealed class InMemoryTransport : IMessageTransport
{
    private readonly Lock _lock = new();
    private readonly List<MessageEnvelope> _sent = [];
    private readonly Dictionary<string, (HashSet<string> Topics, Channel<MessageEnvelope> Channel)> _subscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<MessageEnvelope> _deadLetters = new();

    /// <summary>Everything sent, in order (for assertions in tests).</summary>
    public IReadOnlyList<MessageEnvelope> Sent
    {
        get
        {
            lock (_lock)
            {
                return [.. _sent];
            }
        }
    }

    public IReadOnlyCollection<MessageEnvelope> DeadLetters => [.. _deadLetters];

    public Task SendAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_lock)
        {
            _sent.Add(envelope);
            foreach (var (topics, channel) in _subscriptions.Values)
            {
                if (topics.Contains(envelope.Topic))
                {
                    channel.Writer.TryWrite(envelope);
                }
            }
        }

        return Task.CompletedTask;
    }

    public async Task ConsumeAsync(
        IReadOnlyCollection<string> topics,
        string subscription,
        Func<MessageEnvelope, CancellationToken, Task<ConsumeResult>> onMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentNullException.ThrowIfNull(onMessage);

        Channel<MessageEnvelope> channel;
        lock (_lock)
        {
            if (!_subscriptions.TryGetValue(subscription, out var existing))
            {
                // A new subscription starts from the beginning, like a new consumer group reading from the earliest offset.
                existing = (new HashSet<string>(StringComparer.Ordinal), Channel.CreateUnbounded<MessageEnvelope>());
                _subscriptions[subscription] = existing;
            }

            foreach (var topic in topics.Where(existing.Topics.Add))
            {
                foreach (var sent in _sent.Where(m => m.Topic == topic))
                {
                    existing.Channel.Writer.TryWrite(sent);
                }
            }

            channel = existing.Channel;
        }

        await foreach (var envelope in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await onMessage(envelope, cancellationToken).ConfigureAwait(false) == ConsumeResult.DeadLetter)
            {
                _deadLetters.Enqueue(envelope);
            }
        }
    }
}

/// <summary>Outbox kept in memory (not transactional). Used with the in-memory transport when there is no database.</summary>
public sealed class InMemoryOutbox : IOutboxWriter, IOutboxStore
{
    private readonly Lock _lock = new();
    private readonly List<OutboxMessage> _messages = [];

    public IReadOnlyList<OutboxMessage> Messages
    {
        get
        {
            lock (_lock)
            {
                return [.. _messages];
            }
        }
    }

    public void Add(OutboxMessage message)
    {
        lock (_lock)
        {
            _messages.Add(message);
        }
    }

    public Task<IReadOnlyList<OutboxMessage>> FetchPendingAsync(int max, DateTime utcNow, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<OutboxMessage> pending = [.. _messages
                .Where(m => m.SentAt is null && (m.NextAttemptAt is null || m.NextAttemptAt <= utcNow))
                .OrderBy(m => m.OccurredAt)
                .Take(max)];
            return Task.FromResult(pending);
        }
    }

    public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal int RemoveSentBefore(DateTime beforeUtc)
    {
        lock (_lock)
        {
            return _messages.RemoveAll(m => m.SentAt is { } sent && sent < beforeUtc);
        }
    }
}

/// <summary>Inbox kept in memory: deduplicates within the process' lifetime.</summary>
public sealed class InMemoryInbox : IInbox
{
    private readonly ConcurrentDictionary<(string, string), byte> _processed = new();

    public async Task<bool> ExecuteOnceAsync(string messageId, string consumer, Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_processed.ContainsKey((messageId, consumer)))
        {
            return false;
        }

        await action(cancellationToken).ConfigureAwait(false);
        return _processed.TryAdd((messageId, consumer), 0);
    }
}

/// <summary>Journal kept in memory, next to <see cref="InMemoryOutbox"/>: lost on restart.</summary>
public sealed class InMemoryJournal(InMemoryOutbox outbox) : IMessageJournal
{
    private readonly ConcurrentDictionary<(string, string), ReceivedMessage> _received = new();

    public IReadOnlyCollection<ReceivedMessage> Received => [.. _received.Values];

    public Task RecordAsync(ReceivedMessage entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _received.AddOrUpdate((entry.MessageId, entry.Consumer), entry, (_, existing) => JournalRows.Merge(existing, entry));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> SentSinceAsync(DateTime sinceUtc, int max, CancellationToken cancellationToken)
    {
        IReadOnlyList<OutboxMessage> sent = [.. outbox.Messages.Where(m => m.OccurredAt >= sinceUtc).OrderByDescending(m => m.OccurredAt).Take(max)];
        return Task.FromResult(sent);
    }

    public Task<IReadOnlyList<ReceivedMessage>> ReceivedSinceAsync(DateTime sinceUtc, int max, CancellationToken cancellationToken)
    {
        IReadOnlyList<ReceivedMessage> received = [.. _received.Values.Where(m => m.UpdatedAt >= sinceUtc).OrderByDescending(m => m.UpdatedAt).Take(max)];
        return Task.FromResult(received);
    }

    public Task<int> PurgeAsync(DateTime beforeUtc, CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var (key, row) in _received)
        {
            if (row.UpdatedAt < beforeUtc && _received.TryRemove(key, out _))
            {
                removed++;
            }
        }

        return Task.FromResult(removed + outbox.RemoveSentBefore(beforeUtc));
    }
}

internal static class JournalRows
{
    /// <summary>A later attempt updates the row; the first arrival time stays.</summary>
    public static ReceivedMessage Merge(ReceivedMessage existing, ReceivedMessage update)
    {
        existing.Status = update.Status;
        existing.Attempts = update.Attempts;
        existing.DurationMs = update.DurationMs;
        existing.LastError = update.LastError;
        existing.UpdatedAt = update.UpdatedAt;
        existing.CorrelationId ??= update.CorrelationId;
        existing.TraceId ??= update.TraceId;
        return existing;
    }
}
