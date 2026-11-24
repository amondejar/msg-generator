using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>A change to the event map: the plan of the service it touches and what it implies, ready to review.</summary>
/// <param name="Directory">Absolute folder of the service that changes.</param>
public sealed record EventMapChange(string Service, string Directory, string Summary, IReadOnlyList<string> Notes, ChangePlan Plan);

/// <summary>
/// Edits of the system's event map, one service at a time: publish an entity's events, make a service listen to an
/// event, stop listening. Each returns the plan (nothing is written until <see cref="ChangePlan.Apply"/>), with the
/// same rules as "stratum events on" and "stratum subscribe".
/// </summary>
public static class EventMapEditor
{
    /// <summary>The publisher starts (or stops) publishing Created/Updated/Deleted of an entity.</summary>
    /// <param name="broker">Used only when the service has no messaging yet (defaults to the system's broker).</param>
    public static EventMapChange Publish(string directory, ServiceDefinition service, string entity, bool publish,
        IReadOnlyCollection<ServiceDefinition> system, string? broker = null, GenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(system);
        var notes = new List<string>();
        var updated = service;
        if (publish)
        {
            updated = ServicePlanner.EnsureMessaging(directory, updated, broker ?? SystemBroker(system), out var note);
            if (note is not null)
            {
                notes.Add(note);
            }
        }

        updated = ServicePlanner.SetPublishEvents(updated, entity, publish);
        if (!publish)
        {
            var listeners = system.Where(s => (s.Subscriptions ?? []).Any(x => x.Service == service.Service.Name && x.Entity == entity))
                .Select(s => s.Service.Name).ToList();
            if (listeners.Count > 0)
            {
                notes.Add($"Lo escuchan {string.Join(", ", listeners)}: se quedarán esperando eventos que ya no llegan (el mapa lo marcará).");
            }
        }

        var name = ServicePlanner.FindEntity(service, entity).Name;
        return new EventMapChange(service.Service.Name, directory,
            publish ? $"{service.Service.Name} publica {name}Created, {name}Updated y {name}Deleted" : $"{service.Service.Name} deja de publicar los eventos de {name}",
            notes, ServicePlanner.Plan(directory, service, updated, options));
    }

    /// <summary>The consumer listens to an event of the publisher (copies the contract and creates its handler).</summary>
    public static EventMapChange Subscribe(string consumerDirectory, ServiceDefinition consumer, ServiceDefinition publisher,
        string entity, string eventName, GenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(publisher);
        if (consumer.Service.Name == publisher.Service.Name)
        {
            throw new StratumException("Un servicio no escucha sus propios eventos de integración: dentro del servicio usa los eventos de dominio.");
        }

        var notes = new List<string>();
        var updated = consumer;
        var broker = publisher.Messaging?.Broker;
        if (consumer.Messaging is null)
        {
            updated = ServicePlanner.EnsureMessaging(consumerDirectory, updated, broker, out var note);
            notes.Add(note!);
        }
        else if (broker is not null && consumer.Messaging.Broker != broker)
        {
            notes.Add($"⚠ {consumer.Service.Name} usa {consumer.Messaging.Broker} y {publisher.Service.Name} publica por {broker}: los eventos no le llegarán hasta que usen el mismo broker.");
        }

        var subscription = ServicePlanner.SubscriptionTo(publisher, entity, eventName);
        updated = ServicePlanner.Subscribe(updated, subscription);
        notes.Add($"Se crea el handler {subscription.EventClass}Handler.cs (tuyo): ahí va lo que {consumer.Service.Name} hace con el evento.");
        notes.Add("Regenera el AppHost (Ejecutar) para que el broker conozca la suscripción.");
        return new EventMapChange(consumer.Service.Name, consumerDirectory,
            $"{consumer.Service.Name} escucha {subscription.EventClass} de {publisher.Service.Name}", notes,
            ServicePlanner.Plan(consumerDirectory, consumer, updated, options));
    }

    public static EventMapChange Unsubscribe(string consumerDirectory, ServiceDefinition consumer, string publisher, string entity,
        string eventName, GenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        var updated = ServicePlanner.Unsubscribe(consumer, publisher, entity, eventName);
        return new EventMapChange(consumer.Service.Name, consumerDirectory,
            $"{consumer.Service.Name} deja de escuchar {entity}{eventName} de {publisher}",
            [$"Tu handler {entity}{eventName}Handler.cs se conserva: bórralo si ya no lo necesitas."],
            ServicePlanner.Plan(consumerDirectory, consumer, updated, options));
    }

    /// <summary>The broker most services of the system use, if any.</summary>
    public static string? SystemBroker(IEnumerable<ServiceDefinition> system) =>
        system.Select(s => s.Messaging?.Broker).Where(b => b is MessagingSettings.Kafka or MessagingSettings.AzureServiceBus)
            .GroupBy(b => b).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
}
