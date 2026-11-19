using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Msg.Ddd;

namespace {{Service}}.Infrastructure.Persistence;

/// <summary>
/// Collects the domain events raised by aggregates while saving and publishes them once the
/// transaction has committed, so handlers never react to changes that were rolled back.
/// Handlers run after the commit: if one fails, the data is already saved. For events that must reach
/// other services reliably, publish integration events through an outbox from a handler.
/// </summary>
public sealed class DomainEventsInterceptor(IDomainEventPublisher publisher) : SaveChangesInterceptor
{
    private readonly List<IDomainEvent> _pending = [];

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is { } context)
        {
            // Taken before saving: deleted aggregates are detached afterwards.
            foreach (var aggregate in context.ChangeTracker.Entries<IHasDomainEvents>().Select(e => e.Entity))
            {
                _pending.AddRange(aggregate.DomainEvents);
                aggregate.ClearDomainEvents();
            }
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        var events = _pending.ToArray();
        _pending.Clear();

        foreach (var domainEvent in events)
        {
            await publisher.PublishAsync(domainEvent, cancellationToken).ConfigureAwait(false);
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }
}
