using Microsoft.EntityFrameworkCore;

namespace Stratum.Messaging.EntityFrameworkCore;

/// <summary>Outbox in the service's own database: events are saved with SaveChanges, in the same transaction.</summary>
internal sealed class EfOutbox<TContext>(TContext context) : IOutboxWriter, IOutboxStore
    where TContext : DbContext
{
    public void Add(OutboxMessage message) => context.Set<OutboxMessage>().Add(message);

    public async Task<IReadOnlyList<OutboxMessage>> FetchPendingAsync(int max, DateTime utcNow, CancellationToken cancellationToken) =>
        await context.Set<OutboxMessage>()
            .Where(m => m.SentAt == null && (m.NextAttemptAt == null || m.NextAttemptAt <= utcNow))
            .OrderBy(m => m.OccurredAt)
            .Take(max)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task SaveAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);
}

/// <summary>
/// Runs the handler and records the message in msg_inbox in one transaction: either both happen or neither,
/// so a retried or redelivered message never applies its changes twice.
/// </summary>
internal sealed class EfInbox<TContext>(TContext context, TimeProvider time) : IInbox
    where TContext : DbContext
{
    public async Task<bool> ExecuteOnceAsync(string messageId, string consumer, Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (await context.Set<InboxMessage>().AnyAsync(m => m.MessageId == messageId && m.Consumer == consumer, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await action(cancellationToken).ConfigureAwait(false);

        context.Set<InboxMessage>().Add(new InboxMessage { MessageId = messageId, Consumer = consumer, ProcessedAt = time.GetUtcNow().UtcDateTime });
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Another instance handled the same message at the same time: roll back ours. (After a failed
            // statement PostgreSQL rejects any query in the transaction, so roll back before checking.)
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            if (await IsDuplicateAsync(messageId, consumer).ConfigureAwait(false))
            {
                return false;
            }

            throw;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> IsDuplicateAsync(string messageId, string consumer)
    {
        context.ChangeTracker.Clear();
        return await context.Set<InboxMessage>().AnyAsync(m => m.MessageId == messageId && m.Consumer == consumer, CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>
/// Journal in msg_received and read access to msg_outbox for the diagnostics. Used in its own DI scope, outside the
/// handler's transaction, so failures are recorded too.
/// </summary>
internal sealed class EfJournal<TContext>(TContext context) : IMessageJournal
    where TContext : DbContext
{
    public async Task RecordAsync(ReceivedMessage entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var set = context.Set<ReceivedMessage>();
        var existing = await set.FindAsync([entry.MessageId, entry.Consumer], cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            set.Add(entry);
        }
        else
        {
            JournalRows.Merge(existing, entry);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException) when (existing is null)
        {
            // Another instance recorded the same message at the same moment: theirs is as good as ours.
            context.ChangeTracker.Clear();
        }
    }

    public async Task<IReadOnlyList<OutboxMessage>> SentSinceAsync(DateTime sinceUtc, int max, CancellationToken cancellationToken) =>
        await context.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.OccurredAt >= sinceUtc)
            .OrderByDescending(m => m.OccurredAt)
            .Take(max)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<ReceivedMessage>> ReceivedSinceAsync(DateTime sinceUtc, int max, CancellationToken cancellationToken) =>
        await context.Set<ReceivedMessage>().AsNoTracking()
            .Where(m => m.UpdatedAt >= sinceUtc)
            .OrderByDescending(m => m.UpdatedAt)
            .Take(max)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<int> PurgeAsync(DateTime beforeUtc, CancellationToken cancellationToken)
    {
        var received = await context.Set<ReceivedMessage>().Where(m => m.UpdatedAt < beforeUtc).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var sent = await context.Set<OutboxMessage>().Where(m => m.SentAt != null && m.SentAt < beforeUtc).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return received + sent;
    }

    public async Task<bool> ResendAsync(Guid id, CancellationToken cancellationToken)
    {
        var message = await context.Set<OutboxMessage>().FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (message is null)
        {
            return false;
        }

        OutboxRows.Requeue(message);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        context.Set<OutboxMessage>().Add(message);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
