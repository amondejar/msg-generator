using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>One integration event an entity publishes: Created/Updated/Deleted, or a published operation (Shipped).</summary>
/// <param name="Name">Event name after the entity: "Created", "Shipped", "ItemAdded".</param>
/// <param name="Operation">The operation behind it; null for Created/Updated/Deleted.</param>
public sealed record PublishedEvent(string Service, EntityDefinition Entity, string Name, EntityOperation? Operation)
{
    public string EventClass => Entity.Name + Name;

    public string Topic => Subscription.TopicFor(Service, Entity.Name, Name);

    public bool IsDeleted => Operation is null && Name == Subscription.Deleted;
}

/// <summary>The integration events of a service: what other services can subscribe to, and the data each one carries.</summary>
public static class IntegrationEvents
{
    /// <summary>Events <paramref name="entity"/> publishes (none without messaging).</summary>
    public static IReadOnlyList<PublishedEvent> Of(ServiceDefinition service, EntityDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(entity);
        if (service.Messaging is null || !entity.IsEditable)
        {
            return [];
        }

        var name = service.Service.Name;
        var result = new List<PublishedEvent>();
        result.AddRange(Subscription.Events.Where(entity.PublishesEvent).Select(e => new PublishedEvent(name, entity, e, null)));

        if (service.Architecture == Architectures.CleanDdd)
        {
            foreach (var operation in (entity.Operations ?? []).Where(o => o.Publishes && o.Event.StartsWith(entity.Name, StringComparison.Ordinal) && o.Event.Length > entity.Name.Length))
            {
                result.Add(new PublishedEvent(name, entity, operation.Event[entity.Name.Length..], operation));
            }
        }

        return result;
    }

    /// <summary>
    /// Every event the entity could publish (Created/Updated/Deleted, and its operations in clean-ddd), published or
    /// not: what the designer offers to connect.
    /// </summary>
    public static IReadOnlyList<PublishedEvent> Possible(ServiceDefinition service, EntityDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(entity);
        if (!entity.IsEditable)
        {
            return [];
        }

        var name = service.Service.Name;
        var result = Subscription.Events.Select(e => new PublishedEvent(name, entity, e, null)).ToList();
        if (service.Architecture == Architectures.CleanDdd)
        {
            result.AddRange((entity.Operations ?? [])
                .Where(o => o.Event.StartsWith(entity.Name, StringComparison.Ordinal) && o.Event.Length > entity.Name.Length && !Subscription.Events.Contains(o.Event[entity.Name.Length..]))
                .Select(o => new PublishedEvent(name, entity, o.Event[entity.Name.Length..], o)));
        }

        return result;
    }

    /// <summary>Every event the service publishes.</summary>
    public static IReadOnlyList<PublishedEvent> Of(ServiceDefinition service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return [.. service.Entities.SelectMany(e => Of(service, e))];
    }

    /// <summary>
    /// The data of the event as it travels (besides Id): nothing for Deleted; the entity's data for Created/Updated;
    /// for an operation, the entity's data after it plus the operation's parameters (those not already there).
    /// </summary>
    public static IReadOnlyList<PropertyDefinition> Payload(ServiceDefinition service, PublishedEvent published)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(published);
        if (published.IsDeleted)
        {
            return [];
        }

        var properties = published.Entity.Properties.ToList();
        if (published.Operation is { } operation)
        {
            properties.AddRange(operation.Parameters.Where(p => !properties.Any(x => x.Name == p.Name)));
        }

        return [.. properties.Select(p => PropertyTypes.OnTheWire(p, service.Types))];
    }
}
