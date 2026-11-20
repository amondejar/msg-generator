using Msg.Core.Model;

namespace Msg.Core.Generation;

/// <summary>One integration event of the system: who publishes it and who reacts to it.</summary>
/// <param name="Publisher">Null when a service listens to an event no service of this system publishes.</param>
public sealed record EventRoute(string Topic, string EventClass, string? Publisher, IReadOnlyList<string> Consumers, string? Problem);

/// <summary>Who publishes and who consumes each event, from the msg.json files of a system.</summary>
public static class EventMap
{
    public static IReadOnlyList<EventRoute> Build(IEnumerable<ServiceDefinition> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var all = services.ToList();
        var routes = new Dictionary<string, (string EventClass, string? Publisher, List<string> Consumers, string? Problem)>(StringComparer.Ordinal);

        foreach (var service in all.Where(s => s.Messaging is not null))
        {
            foreach (var entity in service.Entities.Where(e => e.IsEditable && e.Publishes))
            {
                foreach (var name in Subscription.Events)
                {
                    routes[Subscription.TopicFor(service.Service.Name, entity.Name, name)] = (entity.Name + name, service.Service.Name, [], null);
                }
            }
        }

        foreach (var consumer in all)
        {
            var problems = ServicePlanner.CheckSubscriptions(consumer, all).ToDictionary(c => c.Subscription.Topic, c => c.Problem, StringComparer.Ordinal);
            foreach (var subscription in consumer.Subscriptions ?? [])
            {
                if (!routes.TryGetValue(subscription.Topic, out var route))
                {
                    route = (subscription.EventClass, null, [], $"Ningún servicio del sistema publica {subscription.Topic}");
                    routes[subscription.Topic] = route;
                }

                route.Consumers.Add(consumer.Service.Name);
                var publisher = all.FirstOrDefault(s => s.Service.Name == subscription.Service);
                if (publisher?.Messaging is { } published && consumer.Messaging is { } consuming && published.Broker != consuming.Broker)
                {
                    problems.TryAdd(subscription.Topic, $"{published.Broker} en {publisher.Service.Name} y {consuming.Broker} aquí: no le llegarán (mismo broker en los dos)");
                }
                if (problems.TryGetValue(subscription.Topic, out var problem))
                {
                    var text = $"{consumer.Service.Name}: {problem}";
                    routes[subscription.Topic] = route with { Problem = route.Problem is null ? text : $"{route.Problem}; {text}" };
                }
            }
        }

        return [.. routes
            .Select(r => new EventRoute(r.Key, r.Value.EventClass, r.Value.Publisher, r.Value.Consumers, r.Value.Problem))
            .OrderBy(r => r.Topic, StringComparer.Ordinal)];
    }
}
