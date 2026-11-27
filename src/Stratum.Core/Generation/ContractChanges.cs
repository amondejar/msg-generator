using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>What a change of the published events means for one service that listens to them.</summary>
/// <param name="Consumer">The service that listens.</param>
/// <param name="EventClass">The event (CustomerUpdated).</param>
/// <param name="Breaking">True when what it receives no longer matches what it expects (it fails or gets empty data).</param>
/// <param name="Detail">What changes, in words.</param>
/// <param name="Gone">True when the event is no longer published at all.</param>
public sealed record ContractImpact(string Consumer, string EventClass, bool Breaking, string Detail, bool Gone = false);

/// <summary>
/// Compares the events a service publishes before and after a change with what each service of the system that
/// listens to them expects (its copy of the contract in stratum.json): removed or retyped fields it uses, events that
/// are no longer published, and new fields it won't receive until it updates its copy.
/// </summary>
public static class ContractChanges
{
    public static IReadOnlyList<ContractImpact> Impacts(ServiceDefinition before, ServiceDefinition after, IEnumerable<ServiceDefinition> system)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(system);
        var service = before.Service.Name;
        var old = Contracts(before);
        var now = Contracts(after);
        if (Same(old, now))
        {
            return [];
        }

        system = [.. system];

        var impacts = new List<ContractImpact>();
        foreach (var consumer in system.Where(s => s.Service.Name != service))
        {
            foreach (var subscription in (consumer.Subscriptions ?? []).Where(s => s.Service == service))
            {
                if (!old.TryGetValue(subscription.Topic, out var was))
                {
                    continue;   // it didn't match before either: not this change's doing
                }

                var replica = Subscription.Events.Contains(subscription.Event)
                    ? (consumer.Replicas ?? []).FirstOrDefault(r => r.Service == service && r.Entity == subscription.Entity)
                    : null;
                var name = consumer.Service.Name;
                var eventClass = subscription.Entity + subscription.Event;
                if (!now.TryGetValue(subscription.Topic, out var fields))
                {
                    impacts.Add(new(name, eventClass, true,
                        $"{service} deja de publicar {eventClass} y {name} lo escucha{(replica is null ? string.Empty : " (también su réplica)")}: no le llegará nada más.", Gone: true));
                    continue;
                }

                foreach (var expected in subscription.Properties)
                {
                    var before1 = was.FirstOrDefault(p => p.Name == expected.Name);
                    var after1 = fields.FirstOrDefault(p => p.Name == expected.Name);
                    var copied = replica?.Properties.Any(p => p.Name == expected.Name) == true ? " (lo copia su réplica)" : string.Empty;
                    if (before1 is null)
                    {
                        continue;
                    }

                    if (after1 is null)
                    {
                        impacts.Add(new(name, eventClass, true, $"{eventClass} deja de llevar {expected.Name}, que {name} usa{copied}: le llegará vacío."));
                    }
                    else if (Shape(before1) != Shape(after1))
                    {
                        var looser = Shape(before1 with { Nullable = true }) == Shape(after1 with { Nullable = true });
                        impacts.Add(looser && after1.Nullable
                            ? new(name, eventClass, true, $"{eventClass}.{expected.Name} pasa a ser opcional y {name} lo espera siempre{copied}: puede llegarle vacío.")
                            : looser
                                ? new(name, eventClass, false, $"{eventClass}.{expected.Name} pasa a ser obligatorio: {name} lo admite igual.")
                                : new(name, eventClass, true,
                                    $"{eventClass}.{expected.Name} cambia de {Shape(before1)} a {Shape(after1)} y {name} lo usa{copied}: no podrá leer el evento (irá a dead-letter)."));
                    }
                }

                var added = fields.Where(p => !was.Any(x => x.Name == p.Name) && !subscription.Properties.Any(x => x.Name == p.Name)).Select(p => p.Name).ToList();
                if (added.Count > 0)
                {
                    impacts.Add(new(name, eventClass, false,
                        $"{eventClass} añade {string.Join(", ", added)}: {name} lo ignora hasta que actualice su copia del contrato."));
                }
            }
        }

        return impacts;
    }

    /// <summary>Whether the change touches the published events at all (cheap: no need to look at the system otherwise).</summary>
    public static bool Changed(ServiceDefinition before, ServiceDefinition after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return !Same(Contracts(before), Contracts(after));
    }

    /// <summary>One warning per consumer, with what to do after applying.</summary>
    public static IEnumerable<string> Warnings(IReadOnlyList<ContractImpact> impacts)
    {
        ArgumentNullException.ThrowIfNull(impacts);
        foreach (var consumer in impacts.GroupBy(i => i.Consumer))
        {
            var breaking = consumer.Any(i => i.Breaking);
            yield return (breaking ? $"Cambio que rompe el contrato con {consumer.Key}:\n" : $"Cambio compatible para {consumer.Key}:\n")
                         + string.Join("\n", consumer.Select(i => (i.Breaking ? "  ✕ " : "  · ") + i.Detail))
                         + (consumer.Any(i => i.Gone)
                             ? $"\n  Después: stratum subscribe --sync --prune en {consumer.Key} (deja de escuchar lo que ya no se publica; si su réplica lo necesita, se quita)"
                             : $"\n  Después: stratum subscribe --sync en {consumer.Key} (o «Actualizar contrato» en su Mensajería)")
                         + (breaking ? ", y revisa sus handlers. Hasta entonces, despliega los dos servicios a la vez." : ".");
        }
    }

    private static Dictionary<string, IReadOnlyList<PropertyDefinition>> Contracts(ServiceDefinition definition) =>
        IntegrationEvents.Of(definition).GroupBy(e => e.Topic).ToDictionary(g => g.Key, g => IntegrationEvents.Payload(definition, g.First()), StringComparer.Ordinal);

    private static bool Same(Dictionary<string, IReadOnlyList<PropertyDefinition>> a, Dictionary<string, IReadOnlyList<PropertyDefinition>> b) =>
        a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var other)
                                         && x.Value.Count == other.Count
                                         && x.Value.Zip(other).All(p => p.First.Name == p.Second.Name && Shape(p.First) == Shape(p.Second)));

    /// <summary>The type of a field as it travels (what decides whether a consumer can read it).</summary>
    private static string Shape(PropertyDefinition p)
    {
        var type = p.Fields is { Count: > 0 } fields ? "{" + string.Join(", ", fields.Select(f => f.Name + ": " + Shape(f))) + "}" : p.Of ?? p.Type;
        return (p.IsList ? $"lista de {type}" : type) + (p.Nullable ? "?" : string.Empty);
    }
}
