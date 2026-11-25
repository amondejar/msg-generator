using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>
/// Local copies of entities of other services ("stratum replica"): a table kept up to date by a generated handler of
/// the publisher's Created, Updated and Deleted events. The three subscriptions are ordinary ones without a handler of
/// yours (Handler = false); if you already listen to one of them, your handler stays and runs too.
/// </summary>
public static class Replicas
{
    /// <summary>Types a replica can copy: plain values (enumerations travel as text).</summary>
    private static readonly HashSet<string> CopiableTypes = new(StringComparer.Ordinal) { "string", "int", "long", "decimal", "bool", "guid", "date", "datetime" };

    /// <summary>Fields of the publisher's events a replica can copy (Id aside).</summary>
    public static IReadOnlyList<PropertyDefinition> Copiable(Subscription created)
    {
        ArgumentNullException.ThrowIfNull(created);
        return [.. created.Properties.Where(p => !p.IsList && CopiableTypes.Contains(p.Type))];
    }

    /// <summary>
    /// Adds the replica of <paramref name="entityName"/> of <paramref name="publisher"/> to <paramref name="current"/>,
    /// with the given fields (all the copiable ones when null).
    /// </summary>
    public static ServiceDefinition Add(ServiceDefinition current, ServiceDefinition publisher, string entityName, IReadOnlyList<string>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(publisher);
        if (current.Messaging is null)
        {
            throw new StratumException($"{current.Service.Name} necesita mensajería para tener réplicas: stratum messaging on --broker kafka");
        }

        if (current.Persistence is null)
        {
            throw new StratumException($"{current.Service.Name} necesita base de datos para guardar la réplica: añade antes una entidad (o elige la base de datos).");
        }

        if (publisher.Service.Name == current.Service.Name)
        {
            throw new StratumException("Una réplica es de una entidad de otro servicio: dentro del mismo servicio usa una relación (stratum relation add).");
        }

        var entity = ServicePlanner.FindEntity(publisher, entityName);
        var missing = Subscription.Events.Where(e => !entity.PublishesEvent(e)).ToList();
        if (missing.Count > 0)
        {
            throw new StratumException(
                $"{publisher.Service.Name} no publica {string.Join(", ", missing.Select(e => entity.Name + e))}: la réplica necesita los tres eventos. " +
                $"Actívalos en {publisher.Service.Name} (stratum events on {entity.Name}, o «Publicar» en el Studio).");
        }

        if ((current.Replicas ?? []).Any(r => r.Entity == entity.Name))
        {
            throw new StratumException($"{current.Service.Name} ya tiene una réplica de {entity.Name}.");
        }

        if (current.Entities.Any(e => e.Name == entity.Name + "Replica" || e.Plural == entity.Name + "Replicas"))
        {
            throw new StratumException($"{current.Service.Name} ya tiene una entidad {entity.Name}Replica: renómbrala antes.");
        }

        var created = ServicePlanner.SubscriptionTo(publisher, entity.Name, Subscription.Created);
        var copiable = Copiable(created);
        var chosen = fields is null ? copiable : [.. fields.Select(f =>
            copiable.FirstOrDefault(p => p.Name.Equals(f.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new StratumException(created.Properties.Any(p => p.Name.Equals(f.Trim(), StringComparison.OrdinalIgnoreCase))
                ? $"{entity.Name}.{f.Trim()} no se puede copiar en una réplica (listas y elementos compuestos no)."
                : $"{entity.Name} no tiene '{f.Trim()}'. Datos que se pueden copiar: {string.Join(", ", copiable.Select(p => p.Name))}."))];

        var updated = current;
        foreach (var name in Subscription.Events)
        {
            var subscription = ServicePlanner.SubscriptionTo(publisher, entity.Name, name);
            var existing = (updated.Subscriptions ?? []).FirstOrDefault(subscription.Matches);
            if (existing is null)
            {
                updated = updated with { Subscriptions = [.. updated.Subscriptions ?? [], subscription with { Handler = false }] };
            }
        }

        var replica = new ReplicaDefinition { Service = publisher.Service.Name, Entity = entity.Name, Properties = chosen };
        updated = updated with { Replicas = [.. updated.Replicas ?? [], replica] };
        ModelRules.Validate(updated);
        return updated;
    }

    /// <summary>
    /// Removes the replica and the subscriptions it alone used (the ones with a handler of yours stay).
    /// </summary>
    public static ServiceDefinition Remove(ServiceDefinition current, string service, string entity)
    {
        ArgumentNullException.ThrowIfNull(current);
        var replica = Find(current, service, entity);
        var subscriptions = (current.Subscriptions ?? [])
            .Where(s => !(s.Service == replica.Service && s.Entity == replica.Entity && !s.HasHandler))
            .ToList();
        var replicas = current.Replicas!.Where(r => r != replica).ToList();
        return current with
        {
            Replicas = replicas.Count == 0 ? null : replicas,
            Subscriptions = subscriptions.Count == 0 ? null : subscriptions,
        };
    }

    /// <summary>Changes the fields a replica copies.</summary>
    public static ServiceDefinition SetFields(ServiceDefinition current, ServiceDefinition publisher, string entity, IReadOnlyList<string>? fields)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(publisher);
        var replica = Find(current, publisher.Service.Name, entity);
        var withoutIt = current with { Replicas = [.. current.Replicas!.Where(r => r != replica)] };
        var added = Add(withoutIt with { Replicas = withoutIt.Replicas!.Count == 0 ? null : withoutIt.Replicas }, publisher, replica.Entity, fields);
        // Keep the order of the replicas.
        var fresh = added.Replicas!.Last();
        return added with { Replicas = [.. current.Replicas!.Select(r => r == replica ? fresh : r)] };
    }

    public static ReplicaDefinition Find(ServiceDefinition current, string service, string entity)
    {
        ArgumentNullException.ThrowIfNull(current);
        return (current.Replicas ?? []).FirstOrDefault(r =>
                   r.Service.Equals(service, StringComparison.OrdinalIgnoreCase) && r.Entity.Equals(entity, StringComparison.OrdinalIgnoreCase))
               ?? throw new StratumException($"{current.Service.Name} no tiene una réplica de {service}.{entity}. " +
                                             $"Réplicas: {string.Join(", ", (current.Replicas ?? []).Select(r => $"{r.Service}.{r.Entity}").DefaultIfEmpty("ninguna"))}.");
    }

    /// <summary>
    /// After the subscriptions were copied again from the publisher ("subscribe --sync"): the replica keeps the fields
    /// that still exist, with their new type.
    /// </summary>
    public static ServiceDefinition Refresh(ServiceDefinition consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        if (consumer.Replicas is not { Count: > 0 } replicas)
        {
            return consumer;
        }

        var refreshed = replicas.Where(r => Subscription.Events.All(e =>
            (consumer.Subscriptions ?? []).Any(s => s.Service == r.Service && s.Entity == r.Entity && s.Event == e))).Select(r =>
        {
            var created = (consumer.Subscriptions ?? []).FirstOrDefault(s => s.Service == r.Service && s.Entity == r.Entity && s.Event == Subscription.Created);
            if (created is null)
            {
                return r;
            }

            var copiable = Copiable(created);
            return r with { Properties = [.. r.Properties.Select(p => copiable.FirstOrDefault(c => c.Name == p.Name)).OfType<PropertyDefinition>()] };
        }).ToList();
        // A replica that lost one of its events goes, and so do the subscriptions only it used.
        var orphans = (consumer.Subscriptions ?? []).Where(s => !s.HasHandler && !refreshed.Any(r => r.Service == s.Service && r.Entity == s.Entity)).ToList();
        var subscriptions = (consumer.Subscriptions ?? []).Except(orphans).ToList();
        return consumer with
        {
            Replicas = refreshed.Count == 0 ? null : refreshed,
            Subscriptions = subscriptions.Count == 0 ? null : subscriptions,
        };
    }

    /// <summary>Problems of the replicas of a service (for <see cref="ModelRules.Validate"/>).</summary>
    public static IEnumerable<string> Problems(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        foreach (var replica in definition.Replicas ?? [])
        {
            foreach (var name in Subscription.Events)
            {
                if (!(definition.Subscriptions ?? []).Any(s => s.Service == replica.Service && s.Entity == replica.Entity && s.Event == name))
                {
                    yield return $"La réplica de {replica.Service}.{replica.Entity} necesita escuchar {replica.Entity}{name}.";
                }
            }

            if ((definition.Replicas ?? []).Count(r => r.Entity == replica.Entity) > 1)
            {
                yield return $"Dos réplicas de entidades llamadas {replica.Entity}: no pueden convivir en el mismo servicio.";
            }

            foreach (var p in replica.Properties.Where(p => p.IsList || !CopiableTypes.Contains(p.Type)))
            {
                yield return $"La réplica de {replica.Entity} no puede copiar {p.Name} (listas y elementos compuestos no).";
            }

            var created = (definition.Subscriptions ?? []).FirstOrDefault(s => s.Service == replica.Service && s.Entity == replica.Entity && s.Event == Subscription.Created);
            foreach (var p in created is null ? [] : replica.Properties.Where(p => !created.Properties.Any(c => c.Name == p.Name && c.Type == p.Type)))
            {
                yield return $"La réplica de {replica.Entity} copia {p.Name}, que {replica.Entity}Created no trae (o con otro tipo): stratum subscribe --sync la pone al día.";
            }
        }
    }
}
