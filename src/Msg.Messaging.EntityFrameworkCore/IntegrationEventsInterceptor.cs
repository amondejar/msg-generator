using Microsoft.EntityFrameworkCore.Diagnostics;
using Msg.Ddd;

namespace Msg.Messaging.EntityFrameworkCore;

/// <summary>
/// Clean + DDD: before saving, turns the domain events of the aggregates into integration events
/// (<see cref="IIntegrationEventMapper"/>) and adds them to the outbox, so they are committed with the aggregate.
/// Register it before the interceptor that publishes (and clears) the domain events.
/// </summary>
public sealed class IntegrationEventsInterceptor(IEnumerable<IIntegrationEventMapper> mappers) : SaveChangesInterceptor
{
    private readonly IIntegrationEventMapper[] _mappers = [.. mappers];

    // Domain events already mapped by this DbContext (scoped): a second SaveChanges before the events are
    // cleared (e.g. the inbox saving its record) must not publish them twice.
    private readonly HashSet<object> _mapped = new(ReferenceEqualityComparer.Instance);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (_mappers.Length > 0 && eventData.Context is { } context)
        {
            var integrationEvents = context.ChangeTracker.Entries<IHasDomainEvents>()
                .SelectMany(e => e.Entity.DomainEvents.Select(domainEvent => (DomainEvent: domainEvent, Source: e.Entity)))
                .Where(x => _mapped.Add(x.DomainEvent))
                .SelectMany(x => _mappers.SelectMany(m => m.Map(x.DomainEvent, x.Source)))
                .ToList();

            foreach (var integrationEvent in integrationEvents)
            {
                context.Set<OutboxMessage>().Add(OutboxMessages.From(integrationEvent, integrationEvent.GetType()));
            }
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
