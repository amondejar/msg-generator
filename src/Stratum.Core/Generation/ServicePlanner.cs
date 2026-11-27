using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>
/// Computes what Stratum would change in a service folder, without touching the disk. Used by the CLI
/// ("stratum add entity") and by the Studio's review screen, so both always agree.
/// Ownership rules, so user code is never overwritten:
/// <list type="bullet">
/// <item><c>*.g.cs</c> and <c>stratum.json</c> belong to Stratum and are regenerated.</item>
/// <item>Entity, persistence and test-infrastructure files are created only if they don't exist.</item>
/// <item>Project files and appsettings only get the missing entries added.</item>
/// <item>Everything else is left untouched.</item>
/// </list>
/// </summary>
public static class ServicePlanner
{
    private static readonly HashSet<string> ReservedEntityNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ping", "Feature", "Features", "Data", "PagedResponse", "PagedResult", "Service", "Database",
    };

    /// <summary>A brand-new service: every file is created.</summary>
    public static ChangePlan NewService(string directory, ServiceDefinition definition, GenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new StratumException($"La carpeta '{Path.GetFullPath(directory)}' ya existe y no está vacía.");
        }

        ModelRules.Validate(definition);
        var changes = ServiceGenerator.Generate(definition, options)
            .Select(f => new FileChange(f.Path, ChangeKind.Create, f.Content, null))
            .ToList();

        return new ChangePlan(directory, changes, []);
    }

    /// <summary>Normalizes and validates a new entity against the service it is added to.</summary>
    public static EntityDefinition ValidateEntity(EntityDefinition entity, ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(definition);

        var name = ServiceName.Normalize(entity.Name);
        if (ReservedEntityNames.Contains(name))
        {
            throw new StratumException($"'{name}' es un nombre reservado para entidades. Elige otro.");
        }

        var plural = string.IsNullOrWhiteSpace(entity.Plural) ? PropertyTypes.Pluralize(name) : ServiceName.Normalize(entity.Plural);
        if (plural == name)
        {
            throw new StratumException($"El plural de '{name}' no puede ser igual al singular.");
        }

        var clash = definition.Entities.FirstOrDefault(e =>
            e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || e.Plural.Equals(plural, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            throw new StratumException($"Ya existe la entidad '{clash.Name}' ({clash.Plural}).");
        }

        if (entity.Properties.Count == 0)
        {
            throw new StratumException($"La entidad '{name}' necesita al menos una propiedad.");
        }

        // Re-parse through the same rules as --props (names, types, reserved, duplicates).
        // The options of a relation (1–1, on delete) aren't part of --props: they come from the entity as given.
        var spec = PropertyTypes.Spec(entity.Properties);
        // So are the validation rules (length, format, range…).
        var properties = PropertyTypes.Parse(spec, definition.EnumNames, definition.TypeNames).Select(p =>
            (p.References is not null && entity.Properties.FirstOrDefault(x => x.Name == p.Name) is { } given
                ? p with { Unique = given.Unique == true ? true : null, OnDelete = DeleteBehaviors.Normalize(given.OnDelete) is DeleteBehaviors.Restrict or null ? null : DeleteBehaviors.Normalize(given.OnDelete) }
                : p) with { Rules = entity.Properties.FirstOrDefault(x => x.Name == p.Name)?.Rules?.OrNull() }).ToList();
        return new EntityDefinition { Name = name, Plural = plural, Properties = properties, Layout = EntityDefinition.CurrentLayout };
    }

    public static string ResolveProvider(string? requested, PersistenceSettings? current)
    {
        if (requested is not null && !PersistenceSettings.Providers.Contains(requested))
        {
            throw new StratumException($"Base de datos '{requested}' no soportada. Opciones: {string.Join(", ", PersistenceSettings.Providers)}.");
        }

        if (current is null)
        {
            return requested ?? PersistenceSettings.Postgres;
        }

        if (requested is not null && requested != current.Provider)
        {
            throw new StratumException($"Este servicio ya usa '{current.Provider}'. No se puede cambiar de base de datos.");
        }

        return current.Provider;
    }

    /// <summary>Adds entities (already validated) to an existing service.</summary>
    public static ChangePlan AddEntities(string directory, ServiceDefinition current, IReadOnlyList<EntityDefinition> entities, string provider, GenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(entities);

        var updated = current with
        {
            Persistence = new PersistenceSettings { Provider = provider },
            Entities = [.. current.Entities, .. entities],
        };

        return Plan(directory, current, updated, options);
    }

    /// <summary>
    /// New properties for an existing entity. <paramref name="renames"/> says which new names are old
    /// properties renamed (so the migration keeps the data); every other missing property is removed.
    /// </summary>
    public static ServiceDefinition UpdateEntity(
        ServiceDefinition current,
        string entityName,
        IReadOnlyList<PropertyDefinition> properties,
        IReadOnlyList<PropertyRename>? renames = null,
        bool keepRelationOptions = true)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(properties);

        var entity = FindEntity(current, entityName);
        RequireEditable(entity);

        if (properties.Count == 0)
        {
            throw new StratumException($"La entidad '{entity.Name}' necesita al menos una propiedad.");
        }

        var spec = PropertyTypes.Spec(properties);
        var parsed = PropertyTypes.Parse(spec, current.EnumNames, current.TypeNames);

        var pending = (current.PendingRenames ?? []).ToList();
        foreach (var rename in renames ?? [])
        {
            var from = entity.Properties.FirstOrDefault(p => p.Name.Equals(rename.From, StringComparison.OrdinalIgnoreCase))?.Name
                ?? throw new StratumException($"No se puede renombrar '{rename.From}': no es una propiedad de {entity.Name}.");
            var to = parsed.FirstOrDefault(p => p.Name.Equals(rename.To, StringComparison.OrdinalIgnoreCase))?.Name
                ?? throw new StratumException($"Renombrado {from} → {rename.To}: falta la propiedad '{rename.To}' en la nueva lista.");

            if (parsed.Any(p => p.Name == from))
            {
                throw new StratumException($"Renombrado {from} → {to}: '{from}' sigue en la lista de propiedades.");
            }

            if (entity.Properties.Any(p => p.Name == to))
            {
                throw new StratumException($"Renombrado {from} → {to}: '{to}' ya existe en {entity.Name}.");
            }

            // Chain with a rename not yet migrated: A → B, then B → C, is A → C.
            var previous = pending.FirstOrDefault(r => r.Entity == entity.Name && r.To == from);
            if (previous is not null)
            {
                pending.Remove(previous);
                from = previous.From;
            }

            if (from != to)
            {
                pending.Add(new PropertyRename { Entity = entity.Name, From = from, To = to });
            }
        }

        // A renamed property that is now removed: the column is dropped anyway.
        pending.RemoveAll(r => r.Entity == entity.Name && !parsed.Any(p => p.Name == r.To));

        // --props doesn't carry the options of a relation (1–1, on delete): the ones given win (Studio, designer:
        // false / "restrict" to turn them off); otherwise a reference that stays keeps its own.
        // The validation rules too, as long as the type stays (a rule of a text means nothing for a number).
        parsed = [.. parsed.Select(p =>
        {
            var before = renames?.FirstOrDefault(r => r.To.Equals(p.Name, StringComparison.OrdinalIgnoreCase))?.From ?? p.Name;
            var givenAny = properties.FirstOrDefault(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            var oldAny = entity.Properties.FirstOrDefault(o => o.Name.Equals(before, StringComparison.OrdinalIgnoreCase));
            var rules = keepRelationOptions ? givenAny?.Rules ?? (oldAny?.Type == p.Type && oldAny?.IsList == p.IsList ? oldAny.Rules : null) : givenAny?.Rules;
            p = p with { Rules = rules?.OrNull() };
            if (p.References is null)
            {
                return p;
            }

            var given = properties.FirstOrDefault(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase) && x.References == p.References);
            var old = entity.Properties.FirstOrDefault(o => o.Name.Equals(before, StringComparison.OrdinalIgnoreCase) && o.References == p.References);
            var unique = keepRelationOptions ? given?.Unique ?? old?.Unique : given?.Unique;
            var onDelete = DeleteBehaviors.Normalize(keepRelationOptions ? given?.OnDelete ?? old?.OnDelete : given?.OnDelete);
            if (onDelete == DeleteBehaviors.SetNull && !p.Nullable)
            {
                onDelete = null;   // no longer optional: back to the default
            }

            return p with { Unique = unique == true ? true : null, OnDelete = onDelete is null or DeleteBehaviors.Restrict ? null : onDelete };
        })];

        return current with
        {
            Entities = [.. current.Entities.Select(e => e == entity ? entity with { Properties = parsed } : e)],
            PendingRenames = pending.Count == 0 ? null : pending,
        };
    }

    /// <summary>Creates or replaces an enumeration. Removed values are returned so the caller can warn about stored data.</summary>
    public static ServiceDefinition SetEnum(ServiceDefinition current, string name, IReadOnlyList<string> values, out IReadOnlyList<string> removedValues)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(values);
        var normalized = ServiceName.Normalize(name);
        var existing = (current.Enums ?? []).FirstOrDefault(e => e.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        removedValues = existing is null ? [] : [.. existing.Values.Where(v => !values.Contains(v, StringComparer.Ordinal))];
        var updated = current with
        {
            Enums = [.. (current.Enums ?? []).Where(e => e != existing), new EnumDefinition { Name = existing?.Name ?? normalized, Values = values }],
        };
        ModelRules.Validate(updated);
        return updated;
    }

    public static ServiceDefinition RemoveEnum(ServiceDefinition current, string name)
    {
        ArgumentNullException.ThrowIfNull(current);
        var existing = (current.Enums ?? []).FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new StratumException($"No existe el enumerado '{name}'. Enumerados: {string.Join(", ", current.EnumNames.DefaultIfEmpty("ninguno"))}.");
        if (ModelRules.EnumUsages(current, existing.Name) is { Count: > 0 } usages)
        {
            throw new StratumException($"No se puede eliminar {existing.Name}: lo usan {string.Join(", ", usages)}. Cambia antes esas propiedades.");
        }

        var remaining = current.Enums!.Where(e => e != existing).ToList();
        return current with { Enums = remaining.Count == 0 ? null : remaining };
    }

    /// <summary>Adds an item type, or replaces the properties of an existing one ("stratum type add|update").</summary>
    /// <param name="keepRules">Fields without rules keep the ones they had (--props carries none).</param>
    public static ServiceDefinition SetType(ServiceDefinition current, string name, IReadOnlyList<PropertyDefinition> properties, bool keepRules = true)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(properties);
        var normalized = ServiceName.Normalize(name);
        var existing = (current.Types ?? []).FirstOrDefault(t => t.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (keepRules && existing is not null)
        {
            properties = [.. properties.Select(p => p.Rules is null && existing.Properties.FirstOrDefault(o => o.Name == p.Name && o.Type == p.Type) is { Rules: { } rules }
                ? p with { Rules = rules } : p)];
        }

        var type = new ItemTypeDefinition { Name = existing?.Name ?? normalized, Properties = properties };
        var updated = current with
        {
            Types = existing is null ? [.. current.Types ?? [], type] : [.. current.Types!.Select(t => t == existing ? type : t)],
        };
        ModelRules.Validate(updated);
        return updated;
    }

    /// <summary>
    /// Sets (or, with null, removes) the validation rules of a property of an entity or a field of an item type
    /// ("stratum rule set Customer.Email --format email").
    /// </summary>
    public static ServiceDefinition SetRules(ServiceDefinition current, string owner, string property, PropertyRules? rules)
    {
        ArgumentNullException.ThrowIfNull(current);
        rules = rules?.OrNull();
        PropertyDefinition Apply(PropertyDefinition p) => p with { Rules = rules };

        ServiceDefinition updated;
        if (current.Entities.FirstOrDefault(e => e.Name.Equals(owner, StringComparison.OrdinalIgnoreCase)) is { } entity)
        {
            RequireEditable(entity);
            var target = entity.Properties.FirstOrDefault(p => p.Name.Equals(property, StringComparison.OrdinalIgnoreCase))
                ?? throw new StratumException($"{entity.Name} no tiene la propiedad '{property}'. Propiedades: {string.Join(", ", entity.Properties.Select(p => p.Name))}.");
            updated = current with
            {
                Entities = [.. current.Entities.Select(e => e != entity ? e
                    : e with { Properties = [.. e.Properties.Select(p => p == target ? Apply(p) : p)] })],
            };
        }
        else if ((current.Types ?? []).FirstOrDefault(t => t.Name.Equals(owner, StringComparison.OrdinalIgnoreCase)) is { } type)
        {
            var target = type.Properties.FirstOrDefault(p => p.Name.Equals(property, StringComparison.OrdinalIgnoreCase))
                ?? throw new StratumException($"{type.Name} no tiene el campo '{property}'. Campos: {string.Join(", ", type.Properties.Select(p => p.Name))}.");
            updated = current with
            {
                Types = [.. current.Types!.Select(t => t != type ? t
                    : t with { Properties = [.. t.Properties.Select(p => p == target ? Apply(p) : p)] })],
            };
        }
        else
        {
            throw new StratumException($"No existe la entidad ni el tipo de elemento '{owner}'.");
        }

        ModelRules.Validate(updated);
        return updated;
    }

    /// <summary>"ProductId:guid, Quantity:int" → properties of an item type (basic types and enumerations).</summary>
    public static IReadOnlyList<PropertyDefinition> ParseTypeProperties(ServiceDefinition current, string spec)
    {
        ArgumentNullException.ThrowIfNull(current);
        return PropertyTypes.Parse(spec, current.EnumNames);
    }

    public static ServiceDefinition RemoveType(ServiceDefinition current, string name)
    {
        ArgumentNullException.ThrowIfNull(current);
        var existing = (current.Types ?? []).FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new StratumException($"No existe el tipo '{name}'. Tipos: {string.Join(", ", current.TypeNames.DefaultIfEmpty("ninguno"))}.");
        if (ModelRules.TypeUsages(current, existing.Name) is { Count: > 0 } usages)
        {
            throw new StratumException($"No se puede eliminar {existing.Name}: lo usan {string.Join(", ", usages)}. Cambia antes esas propiedades.");
        }

        var remaining = current.Types!.Where(t => t != existing).ToList();
        return current with { Types = remaining.Count == 0 ? null : remaining };
    }

    public static ServiceDefinition AddOperation(ServiceDefinition current, string entityName, EntityOperation operation)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(operation);
        var entity = FindEntity(current, entityName);
        RequireEditable(entity);
        var updated = current with
        {
            Entities = [.. current.Entities.Select(e => e == entity ? e with { Operations = [.. e.Operations ?? [], operation] } : e)],
        };
        ModelRules.Validate(updated);
        return updated;
    }

    /// <summary>Replaces the operations of an entity (Studio): validated with the rest of the model in Plan.</summary>
    public static ServiceDefinition SetOperations(ServiceDefinition current, string entityName, IReadOnlyList<EntityOperation>? operations)
    {
        ArgumentNullException.ThrowIfNull(current);
        var entity = FindEntity(current, entityName);
        return current with
        {
            Entities = [.. current.Entities.Select(e => e == entity ? e with { Operations = operations is { Count: > 0 } ? operations : null } : e)],
        };
    }

    /// <summary>Publishes (or stops publishing) an operation as an integration event of its own (OrderShipped).</summary>
    public static ServiceDefinition PublishOperation(ServiceDefinition current, string entityName, string operationName, bool publish)
    {
        ArgumentNullException.ThrowIfNull(current);
        var entity = FindEntity(current, entityName);
        var operation = (entity.Operations ?? []).FirstOrDefault(o => o.Name.Equals(operationName, StringComparison.OrdinalIgnoreCase))
            ?? throw new StratumException($"{entity.Name} no tiene la operación '{operationName}'. Operaciones: {string.Join(", ", (entity.Operations ?? []).Select(o => o.Name).DefaultIfEmpty("ninguna"))}.");
        var updated = current with
        {
            Entities = [.. current.Entities.Select(e => e != entity ? e : e with
            {
                Operations = [.. e.Operations!.Select(o => o == operation ? o with { Publish = publish ? true : null } : o)],
            })],
        };
        ModelRules.Validate(updated);
        return updated;
    }

    public static ServiceDefinition RemoveOperation(ServiceDefinition current, string entityName, string operationName)
    {
        ArgumentNullException.ThrowIfNull(current);
        var entity = FindEntity(current, entityName);
        var operation = (entity.Operations ?? []).FirstOrDefault(o => o.Name.Equals(operationName, StringComparison.OrdinalIgnoreCase))
            ?? throw new StratumException($"{entity.Name} no tiene la operación '{operationName}'. Operaciones: {string.Join(", ", (entity.Operations ?? []).Select(o => o.Name).DefaultIfEmpty("ninguna"))}.");
        var remaining = entity.Operations!.Where(o => o != operation).ToList();
        return current with
        {
            Entities = [.. current.Entities.Select(e => e == entity ? e with { Operations = remaining.Count == 0 ? null : remaining } : e)],
        };
    }

    public static ServiceDefinition RemoveEntity(ServiceDefinition current, string entityName)
    {
        ArgumentNullException.ThrowIfNull(current);
        var entity = FindEntity(current, entityName);
        if (ModelRules.Dependents(current, entity.Name) is { Count: > 0 } dependents)
        {
            throw new StratumException($"No se puede eliminar {entity.Name}: la usan {string.Join(", ", dependents.Select(d => $"{d.Entity.Name}.{d.Property.Name}"))}. Quita antes esas referencias.");
        }

        var pending = (current.PendingRenames ?? []).Where(r => r.Entity != entity.Name).ToList();

        // Persistence stays: the database (and its migrations) still exist.
        return current with
        {
            Entities = [.. current.Entities.Where(e => e != entity)],
            PendingRenames = pending.Count == 0 ? null : pending,
        };
    }

    /// <summary>An entity created before Stratum 0.10 moves to the editable layout (*.g.cs + partial classes).</summary>
    public static ServiceDefinition ConvertEntity(ServiceDefinition current, string entityName)
    {
        ArgumentNullException.ThrowIfNull(current);
        var entity = FindEntity(current, entityName);
        if (entity.IsEditable)
        {
            throw new StratumException($"La entidad '{entity.Name}' ya tiene el formato editable.");
        }

        return current with
        {
            Entities = [.. current.Entities.Select(e => e == entity ? e with { Layout = EntityDefinition.CurrentLayout } : e)],
        };
    }

    /// <summary>True when Program.cs delegates to the generated StratumWiring.g.cs (services from Stratum 0.12 on, or converted).</summary>
    public static bool UsesStratumWiring(string directory, ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var program = Path.Combine(directory, "src", $"{definition.Service.Name}.Api", "Program.cs");
        return File.Exists(program) && File.ReadAllText(program).Contains("AddStratumWiring()", StringComparison.Ordinal);
    }

    /// <summary>Turns features on or off in an existing service (the wiring lives in StratumWiring.g.cs).</summary>
    public static ServiceDefinition SetFeatures(string directory, ServiceDefinition current, FeatureSet features)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(features);

        if (features != current.Features && !UsesStratumWiring(directory, current))
        {
            throw new StratumException(
                $"src/{current.Service.Name}.Api/Program.cs es de una versión de Stratum anterior a la 0.12 y configura las features a mano, " +
                "así que Stratum no puede activarlas ni desactivarlas sin pisarlo. Pásalo a StratumWiring.g.cs primero " +
                "(stratum feature convert --dry-run, o desde el Studio); Stratum te enseñará el cambio antes de escribir nada.");
        }

        return current with { Features = features };
    }

    /// <summary>Turns messaging on (with a broker) or off (<paramref name="broker"/> null). Lives in StratumWiring.g.cs, like the features.</summary>
    public static ServiceDefinition SetMessaging(string directory, ServiceDefinition current, string? broker)
    {
        ArgumentNullException.ThrowIfNull(current);
        var messaging = broker is null ? null : new MessagingSettings { Broker = MessagingSettings.Validate(broker) };
        if (messaging == current.Messaging)
        {
            return current;
        }

        if (!UsesStratumWiring(directory, current))
        {
            throw new StratumException(
                $"src/{current.Service.Name}.Api/Program.cs es de una versión de Stratum anterior a la 0.12: pásalo a StratumWiring.g.cs " +
                "primero (stratum feature convert --dry-run, o desde el Studio) para que Stratum pueda configurar la mensajería.");
        }

        return current with { Messaging = messaging };
    }

    /// <summary>
    /// Turns messaging on when something needs it (publishing or listening to events), instead of failing: with
    /// <paramref name="broker"/>, else the one most services of the system use, else Kafka. Null note when it was on.
    /// </summary>
    public static ServiceDefinition EnsureMessaging(string directory, ServiceDefinition current, string? broker, out string? note)
    {
        ArgumentNullException.ThrowIfNull(current);
        note = null;
        if (current.Messaging is not null)
        {
            return current;
        }

        var chosen = broker ?? SystemBroker(directory) ?? MessagingSettings.Kafka;
        note = $"Mensajería activada en {current.Service.Name} con {chosen} (outbox e inbox): hace falta para publicar o escuchar eventos. Necesita una migración nueva.";
        return SetMessaging(directory, current, chosen);
    }

    /// <summary>The broker most services next to this one use (its system), if any.</summary>
    public static string? SystemBroker(string serviceDirectory)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(serviceDirectory));
        if (parent is null || !Directory.Exists(parent))
        {
            return null;
        }

        try
        {
            return EventMapEditor.SystemBroker(AspireGenerator.Discover(parent, maxDepth: 2).Select(s => s.Definition));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or StratumException)
        {
            return null;
        }
    }

    /// <summary>
    /// Publishes (or stops publishing) one event of an entity: Created / Updated / Deleted, or the event of an
    /// operation (Shipped). The other events of the entity stay as they were. Messaging must be on to publish.
    /// </summary>
    public static ServiceDefinition SetPublishedEvent(ServiceDefinition current, string entityName, string eventName, bool publish)
    {
        ArgumentNullException.ThrowIfNull(current);
        var entity = FindEntity(current, entityName);
        if (IntegrationEvents.Possible(current, entity).FirstOrDefault(e => e.Name.Equals(eventName, StringComparison.OrdinalIgnoreCase)) is not { } possible)
        {
            throw new StratumException($"{entity.Name} no tiene un evento '{eventName}'. Puede publicar: {string.Join(", ", IntegrationEvents.Possible(current, entity).Select(e => e.Name))}.");
        }

        if (possible.Operation is { } operation)
        {
            return PublishOperation(current, entity.Name, operation.Name, publish);
        }

        if (publish && current.Messaging is null)
        {
            throw new StratumException($"{current.Service.Name} no tiene mensajería: actívala antes de publicar {entity.Name}{possible.Name}.");
        }

        var set = Subscription.Events.Where(entity.PublishesEvent).ToHashSet(StringComparer.Ordinal);
        if (publish)
        {
            set.Add(possible.Name);
        }
        else
        {
            set.Remove(possible.Name);
        }

        var updated = entity with
        {
            PublishEvents = set.Count == 0 ? null : true,
            PublishedEvents = set.Count is 0 or 3 ? null : [.. Subscription.Events.Where(set.Contains)],
        };
        return current with { Entities = [.. current.Entities.Select(e => e == entity ? updated : e)] };
    }

    /// <summary>Publish (or stop publishing) the Created/Updated/Deleted integration events of an entity.</summary>
    public static ServiceDefinition SetPublishEvents(ServiceDefinition current, string entityName, bool publish)
    {
        ArgumentNullException.ThrowIfNull(current);
        var entity = FindEntity(current, entityName);
        RequireEditable(entity);

        if (publish && current.Messaging is null)
        {
            throw new StratumException(
                "Para publicar eventos el servicio necesita mensajería. Actívala antes: stratum messaging on --broker kafka");
        }

        return current with
        {
            Entities = [.. current.Entities.Select(e => e == entity ? e with { PublishEvents = publish ? true : null, PublishedEvents = null } : e)],
        };
    }

    /// <summary>The subscription to one event of <paramref name="publisher"/>, with the event's current data.</summary>
    public static Subscription SubscriptionTo(ServiceDefinition publisher, string entityName, string eventName)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        var entity = FindEntity(publisher, entityName);
        var events = IntegrationEvents.Of(publisher, entity);
        var published = events.FirstOrDefault(e => e.Name.Equals(eventName?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (published is null)
        {
            if (events.Count == 0)
            {
                throw new StratumException(
                    $"{publisher.Service.Name} no publica eventos de {entity.Name}. Actívalo en ese servicio: " +
                    $"stratum events on {entity.Name} (y stratum messaging on si no tiene mensajería), o publica una operación (--publish).");
            }

            throw new StratumException($"{publisher.Service.Name} no publica {entity.Name}{eventName}. Publica: {string.Join(", ", events.Select(e => e.Name))}.");
        }

        return new Subscription
        {
            Service = publisher.Service.Name,
            Entity = entity.Name,
            Event = published.Name,
            Properties = IntegrationEvents.Payload(publisher, published),
        };
    }

    public static ServiceDefinition Subscribe(ServiceDefinition current, Subscription subscription)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(subscription);

        if (current.Messaging is null)
        {
            throw new StratumException($"{current.Service.Name} necesita mensajería para escuchar eventos: stratum messaging on --broker kafka");
        }

        var existing = current.Subscriptions ?? [];
        if (existing.FirstOrDefault(subscription.Matches) is { } already)
        {
            // A replica listened already, without a handler of yours: now you get one too.
            return already.HasHandler || !subscription.HasHandler
                ? current
                : current with { Subscriptions = [.. existing.Select(s => s == already ? s with { Handler = null } : s)] };
        }

        return current with { Subscriptions = [.. existing, subscription] };
    }

    public static ServiceDefinition Unsubscribe(ServiceDefinition current, string service, string entity, string eventName)
    {
        ArgumentNullException.ThrowIfNull(current);
        var existing = current.Subscriptions ?? [];
        bool Same(Subscription s) => s.Service.Equals(service, StringComparison.OrdinalIgnoreCase)
                                     && s.Entity.Equals(entity, StringComparison.OrdinalIgnoreCase)
                                     && s.Event.Equals(eventName, StringComparison.OrdinalIgnoreCase);
        if (!existing.Any(Same))
        {
            throw new StratumException($"{current.Service.Name} no escucha {service} {entity}{eventName}.");
        }

        // A replica still needs the event: only your handler goes.
        if ((current.Replicas ?? []).Any(r => r.Service.Equals(service, StringComparison.OrdinalIgnoreCase) && r.Entity.Equals(entity, StringComparison.OrdinalIgnoreCase)))
        {
            return current with { Subscriptions = [.. existing.Select(s => Same(s) ? s with { Handler = false } : s)] };
        }

        var remaining = existing.Where(s => !Same(s)).ToList();
        return current with { Subscriptions = remaining.Count == 0 ? null : remaining };
    }

    /// <summary>Subscriptions whose copied contract no longer matches the publisher (or that it no longer publishes).</summary>
    public static IReadOnlyList<(Subscription Subscription, Subscription? Current, string Problem)> CheckSubscriptions(
        ServiceDefinition consumer, IEnumerable<ServiceDefinition> services)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        var all = services.ToList();
        var result = new List<(Subscription, Subscription?, string)>();
        foreach (var subscription in consumer.Subscriptions ?? [])
        {
            var publisher = all.FirstOrDefault(s => s.Service.Name == subscription.Service);
            if (publisher is null)
            {
                continue;   // not in this workspace: nothing to compare with
            }

            try
            {
                var latest = SubscriptionTo(publisher, subscription.Entity, subscription.Event);
                if (!SameProperties(latest.Properties, subscription.Properties))
                {
                    result.Add((subscription, latest, $"{subscription.Service} ha cambiado los datos de {subscription.EventClass}"));
                }
            }
            catch (StratumException ex)
            {
                result.Add((subscription, null, ex.Message));
            }
        }

        return result;
    }

    /// <summary>Copies again the contracts of the subscriptions from their publishers.</summary>
    /// <param name="prune">Also drop subscriptions to events their publisher no longer publishes (entity removed, events off).</param>
    public static ServiceDefinition SyncSubscriptions(ServiceDefinition consumer, IEnumerable<ServiceDefinition> services, bool prune = false)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        var checks = CheckSubscriptions(consumer, services);
        var updates = checks.Where(c => c.Current is not null).ToList();
        var gone = prune ? checks.Where(c => c.Current is null).Select(c => c.Subscription).ToHashSet() : [];
        if (updates.Count == 0 && gone.Count == 0)
        {
            return consumer;
        }

        var remaining = (consumer.Subscriptions ?? [])
            .Where(s => !gone.Contains(s))
            .Select(s => updates.FirstOrDefault(u => u.Subscription == s).Current is { } latest ? latest with { Handler = s.Handler } : s)
            .ToList();
        return Replicas.Refresh(consumer with { Subscriptions = remaining.Count == 0 ? null : remaining });
    }

    private static bool SameProperties(IReadOnlyList<PropertyDefinition>? a, IReadOnlyList<PropertyDefinition>? b) =>
        a is null || b is null ? a is null && b is null
            : a.Count == b.Count && a.Zip(b).All(p => p.First with { Fields = null } == p.Second with { Fields = null } && SameProperties(p.First.Fields, p.Second.Fields));

    /// <summary>
    /// Rewrites a Program.cs from before Stratum 0.12 so it delegates to StratumWiring.g.cs. The user's own lines must be
    /// added back (the plan shows the previous file).
    /// </summary>
    public static ChangePlan ConvertWiring(string directory, ServiceDefinition current, GenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (UsesStratumWiring(directory, current))
        {
            throw new StratumException("Program.cs ya usa StratumWiring: las features se pueden cambiar con 'stratum feature on|off'.");
        }

        var service = current.Service.Name;
        var files = ServiceGenerator.Generate(current, options);
        var changes = new List<FileChange>();
        foreach (var relative in new[] { $"src/{service}.Api/Program.cs", $"src/{service}.Api/StratumWiring.g.cs" })
        {
            var file = files.First(f => f.Path == relative);
            var target = Path.Combine(directory, relative);
            var previous = File.Exists(target) ? File.ReadAllText(target) : null;
            if (previous is null || !Same(previous, file.Content))
            {
                var kind = previous is null ? ChangeKind.Create : relative.EndsWith(".g.cs", StringComparison.Ordinal) ? ChangeKind.Regenerate : ChangeKind.Replace;
                changes.Add(new FileChange(relative, kind, file.Content, previous));
            }
        }

        return new ChangePlan(directory, changes,
        [
            "Program.cs pasa a llamar a builder.AddStratumWiring() y app.UseStratumWiring(). Si habías añadido servicios o middleware " +
            "propios, vuelve a ponerlos (después de AddStratumWiring y de UseStratumWiring): el diff muestra tu versión anterior.",
        ]);
    }

    public static EntityDefinition FindEntity(ServiceDefinition definition, string name)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Entities.FirstOrDefault(e => e.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new StratumException(
                $"No existe la entidad '{name}'. Entidades: {(definition.Entities.Count == 0 ? "(ninguna)" : string.Join(", ", definition.Entities.Select(e => e.Name)))}.");
    }

    internal static void RequireEditable(EntityDefinition entity)
    {
        if (!entity.IsEditable)
        {
            throw new StratumException(
                $"La entidad '{entity.Name}' se creó con una versión de Stratum anterior a la 0.10: su código es tuyo y Stratum no puede " +
                $"cambiarla sin pisarlo. Conviértela primero al formato editable (stratum entity convert {entity.Name}, o desde el Studio); " +
                "Stratum te enseñará qué archivos cambia antes de escribir nada.");
        }
    }

    /// <summary>
    /// Everything Stratum has to write, replace or delete to go from <paramref name="current"/> to
    /// <paramref name="updated"/> (the stratum.json on disk and the one designed in the CLI or Studio).
    /// </summary>
    /// <remarks>
    /// Rules: *.g.cs are always rewritten (with a warning if edited by hand). Any other file Stratum generated is
    /// updated or deleted only while it is exactly as Stratum left it; once the user changed it, Stratum reports instead.
    /// Project files get missing items added and the items of disabled features removed.
    /// </remarks>
    /// <param name="deleteOwnFiles">Also delete the user's (modified) files of removed entities.</param>
    /// <summary>
    /// The services next to this one (its system: the folder that holds it, as "stratum aspire" sees it), to warn the
    /// ones that listen to its events. Empty when it can't be read.
    /// </summary>
    public static IReadOnlyList<ServiceDefinition> SystemAround(string directory)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(directory));
            return parent is null ? [] : [.. AspireGenerator.Discover(parent, 3).Select(s => s.Definition)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StratumException)
        {
            return [];
        }
    }

    public static ChangePlan Plan(string directory, ServiceDefinition current, ServiceDefinition updated, GenerationOptions? options = null, bool deleteOwnFiles = false,
        IEnumerable<ServiceDefinition>? system = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(updated);
        ModelRules.Validate(updated);

        var files = ServiceGenerator.Generate(updated, options);
        var newPaths = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var oldFiles = ServiceGenerator.Generate(current, options).ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);
        var service = current.Service.Name;
        var changes = new List<FileChange>();
        var warnings = new List<string>();
        var handled = new HashSet<string>(StringComparer.Ordinal);
        var ownLeft = new List<string>();
        var modifiedLeft = new List<string>();
        var obsoleteLeft = new List<string>();

        string[] projectFiles =
        [
            "Directory.Packages.props",
            $"src/{service}.Api/{service}.Api.csproj",
            $"src/{service}.Infrastructure/{service}.Infrastructure.csproj",
            $"src/{service}.Application/{service}.Application.csproj",
            $"src/{service}.Domain/{service}.Domain.csproj",
            $"tests/{service}.Api.Tests/{service}.Api.Tests.csproj",
        ];
        var appSettingsDevelopment = $"src/{service}.Api/appsettings.Development.json";
        var merged = new HashSet<string>([.. projectFiles, appSettingsDevelopment, ServiceGenerator.DefinitionFileName], StringComparer.Ordinal);

        // 1. Entities converted to the editable layout: their old files are replaced or deleted (the user asked for it).
        foreach (var entity in updated.Entities.Where(e => e.IsEditable))
        {
            if (current.Entities.FirstOrDefault(e => e.Name == entity.Name) is not { IsEditable: false } legacy)
            {
                continue;
            }

            var entityFiles = ServiceGenerator.GenerateEntity(updated, entity, options).ToDictionary(f => f.Path, StringComparer.Ordinal);
            foreach (var path in ServiceGenerator.LegacyEntityPaths(current, legacy))
            {
                if (Read(path) is not { } previous)
                {
                    continue;
                }

                handled.Add(path);
                if (!entityFiles.TryGetValue(path, out var file))
                {
                    changes.Add(new FileChange(path, ChangeKind.Delete, string.Empty, previous));
                }
                else if (!KeptOnConversion(path))
                {
                    changes.Add(new FileChange(path, ChangeKind.Replace, file.Content, previous));
                }
            }
        }

        // 2. Generated files.
        foreach (var file in files)
        {
            var previous = Read(file.Path);
            if (handled.Contains(file.Path) || file.Path == ServiceGenerator.DefinitionFileName || (merged.Contains(file.Path) && previous is not null))
            {
                continue;
            }

            oldFiles.TryGetValue(file.Path, out var oldRender);

            if (file.Path.EndsWith(".g.cs", StringComparison.Ordinal))
            {
                if (previous is null || !Same(previous, file.Content))
                {
                    var edited = previous is not null && GeneratedFileHash.IsHandEdited(previous);
                    changes.Add(new FileChange(file.Path, previous is null ? ChangeKind.Create : ChangeKind.Regenerate, file.Content, previous) { HandEdited = edited });
                }
            }
            else if (previous is null)
            {
                // New in this generation (new entity, feature turned on). A file the user deleted on purpose
                // (e.g. an event handler) was already in the previous generation, so it is not brought back.
                if (oldRender is null)
                {
                    changes.Add(new FileChange(file.Path, ChangeKind.Create, file.Content, null));
                }
            }
            else if (oldRender is not null && !Same(oldRender, file.Content))
            {
                // The change affects this file (e.g. appsettings.json when auth is turned on).
                if (Same(previous, oldRender))
                {
                    changes.Add(new FileChange(file.Path, ChangeKind.Regenerate, file.Content, previous));
                }
                else if (file.Path != "README.md")
                {
                    modifiedLeft.Add(file.Path);
                }
            }
        }

        // 3. Files no longer generated (removed entities, features turned off).
        foreach (var (path, oldRender) in oldFiles.Where(f => !newPaths.Contains(f.Key) && f.Key != ServiceGenerator.DefinitionFileName).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (Read(path) is not { } previous)
            {
                continue;
            }

            if (path.EndsWith(".g.cs", StringComparison.Ordinal) || Same(previous, oldRender))
            {
                // Generated, or exactly as Stratum left it: nothing of the user's is lost.
                changes.Add(new FileChange(path, ChangeKind.Delete, string.Empty, previous) { IsUnchangedDelete = true });
            }
            else if (IsCreateOnce(path, service))
            {
                DeleteOwn(path, previous);
            }
            else
            {
                obsoleteLeft.Add(path);
            }
        }

        foreach (var legacy in current.Entities.Where(e => !e.IsEditable && !updated.Entities.Any(u => u.Name == e.Name)))
        {
            foreach (var path in ServiceGenerator.LegacyEntityPaths(current, legacy))
            {
                if (Read(path) is { } previous)
                {
                    DeleteOwn(path, previous);
                }
            }
        }

        if (ownLeft.Count > 0)
        {
            warnings.Add(
                "Estos archivos tuyos son de entidades eliminadas y los has modificado, así que Stratum no los borra: probablemente ya no compilen. " +
                "Bórralos tú o marca \"borrar también mis archivos\":\n" + string.Join("\n", ownLeft.Select(p => "      " + p)));
        }

        if (modifiedLeft.Count > 0)
        {
            warnings.Add(
                "Estos archivos cambiarían con este cambio, pero los has modificado y Stratum no los pisa. Revisa a mano lo que necesitan " +
                "(la vista previa muestra cómo los generaría Stratum):\n" + string.Join("\n", modifiedLeft.Select(p => "      " + p)));
        }

        if (obsoleteLeft.Count > 0)
        {
            warnings.Add(
                "Estos archivos ya no hacen falta, pero los has modificado y Stratum no los borra. Bórralos tú si ya no los usas:\n" +
                string.Join("\n", obsoleteLeft.Select(p => "      " + p)));
        }

        // 4. Project files: missing items added, items of what was turned off removed. appsettings: connection strings.
        var packagesUpgraded = false;
        foreach (var projectFile in projectFiles)
        {
            if (Read(projectFile) is not { } previous)
            {
                continue;
            }

            var rendered = files.FirstOrDefault(f => f.Path == projectFile)?.Content;
            if (rendered is null)
            {
                // The whole project is gone (e.g. tests turned off) and handled as a deleted file above.
                continue;
            }

            var renamedPrevious = ProjectFileMerger.RenameStratumPackages(previous) ?? previous;   // Stratum.* → StratumKit.*
            var text = oldFiles.TryGetValue(projectFile, out var oldRender)
                ? ProjectFileMerger.RemoveItems(renamedPrevious, oldRender, rendered) ?? renamedPrevious
                : renamedPrevious;
            text = ProjectFileMerger.MergeItems(text, rendered) ?? text;
            if (Path.GetFileName(projectFile) == ServiceUpgrader.PackagesFileName
                && ProjectFileMerger.AlignStratumVersions(text, StratumInfo.Version) is { } aligned)
            {
                text = aligned;
                packagesUpgraded = true;
            }

            if (text != previous)
            {
                changes.Add(new FileChange(projectFile, ChangeKind.Merge, text, previous));
            }
        }

        // nuget.config from before 0.36.1: its local source maps Stratum.*, the packages are StratumKit.* now.
        if (Read("nuget.config") is { } nugetConfig && ProjectFileMerger.RenameStratumPackages(nugetConfig) is { } renamedConfig
            && !changes.Any(c => c.Path == "nuget.config"))
        {
            changes.Add(new FileChange("nuget.config", ChangeKind.Merge, renamedConfig, nugetConfig));
        }

        if (files.FirstOrDefault(f => f.Path == appSettingsDevelopment) is { } settings
            && Read(appSettingsDevelopment) is { } settingsPrevious
            && ProjectFileMerger.MergeConnectionStrings(settingsPrevious, settings.Content) is { } settingsMerged)
        {
            changes.Add(new FileChange(appSettingsDevelopment, ChangeKind.Merge, settingsMerged, settingsPrevious));
        }

        // 5. stratum.json. stratumVersion follows the Stratum.* packages: it only changes when they were brought to this
        //    version above (the generated code goes with its packages; the plan shows Directory.Packages.props).
        var definition = ServiceGenerator.Serialize(updated with { StratumVersion = packagesUpgraded ? StratumInfo.Version : current.StratumVersion });
        var definitionPrevious = Read(ServiceGenerator.DefinitionFileName);
        if (definitionPrevious is null || !Same(definition, definitionPrevious) || changes.Count > 0)
        {
            changes.Add(new FileChange(ServiceGenerator.DefinitionFileName, ChangeKind.Regenerate, definition, definitionPrevious));
        }

        // 6. The documentation (docs/), once the service has it: the specification follows the model, and the new
        //    operations and subscriptions get their user story.
        if (SpecGenerator.HasDocs(directory))
        {
            changes.AddRange(SpecGenerator.Changes(directory, SpecGenerator.ForService(directory, updated)));
        }

        var handEdited = changes.Where(c => c.HandEdited).Select(c => c.Path).ToList();
        if (handEdited.Count > 0)
        {
            warnings.Add(
                "Has editado a mano estos archivos generados; al aplicar se pierden esos cambios. Pásalos a la clase partial " +
                "(el archivo sin .g), o a las plantillas del equipo si son para todos los servicios:\n" +
                string.Join("\n", handEdited.Select(p => "      " + p)));
        }

        warnings.AddRange(CheckReferences(directory, current, updated));
        warnings.AddRange(Relations.Warnings(current, updated));
        if (ContractChanges.Changed(current, updated))
        {
            warnings.AddRange(ContractChanges.Warnings(ContractChanges.Impacts(current, updated, system ?? SystemAround(directory))));
        }
        warnings.AddRange(CheckWiring(directory, service, current.Features));

        return new ChangePlan(directory, changes, warnings);

        string? Read(string relative)
        {
            var target = Path.Combine(directory, relative);
            return File.Exists(target) ? File.ReadAllText(target) : null;
        }

        void DeleteOwn(string path, string previous)
        {
            if (deleteOwnFiles)
            {
                changes.Add(new FileChange(path, ChangeKind.Delete, string.Empty, previous));
            }
            else
            {
                ownLeft.Add(path);
            }
        }
    }

    /// <summary>Same text ignoring line endings (git may check files out with CRLF on Windows).</summary>
    internal static bool Same(string a, string b) =>
        a.Replace("\r\n", "\n", StringComparison.Ordinal) == b.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>On conversion these keep their path and content (they don't depend on the properties).</summary>
    private static bool KeptOnConversion(string path) =>
        path.EndsWith("Events.cs", StringComparison.Ordinal)
        || path.EndsWith("Repository.cs", StringComparison.Ordinal)
        || path.EndsWith("EventHandlers.cs", StringComparison.Ordinal);

    /// <summary>Removed or renamed properties still used in the user's files of that entity.</summary>
    private static List<string> CheckReferences(string root, ServiceDefinition current, ServiceDefinition updated)
    {
        var warnings = new List<string>();
        foreach (var entity in updated.Entities.Where(e => e.IsEditable))
        {
            if (current.Entities.FirstOrDefault(e => e.Name == entity.Name) is not { IsEditable: true } before)
            {
                continue;
            }

            var gone = before.Properties.Where(p => !entity.Properties.Any(n => n.Name == p.Name)).ToList();
            if (gone.Count == 0)
            {
                continue;
            }

            // Renames of this change: new entries in pendingRenames (stored with the original name, A → C after A → B → C).
            var renamedTo = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rename in (updated.PendingRenames ?? []).Where(r => r.Entity == entity.Name && !(current.PendingRenames ?? []).Contains(r)))
            {
                var name = before.Properties.Any(p => p.Name == rename.From)
                    ? rename.From
                    : (current.PendingRenames ?? []).FirstOrDefault(r => r.Entity == entity.Name && r.From == rename.From)?.To;
                if (name is not null)
                {
                    renamedTo[name] = rename.To;
                }
            }

            var userFiles = ServiceGenerator.GenerateEntity(updated, entity)
                .Select(f => f.Path)
                .Where(p => !p.EndsWith(".g.cs", StringComparison.Ordinal) && File.Exists(Path.Combine(root, p)))
                .ToList();

            foreach (var property in gone)
            {
                var pattern = new System.Text.RegularExpressions.Regex($@"\b{property.Name}\b");
                var usedIn = userFiles.Where(p => pattern.IsMatch(File.ReadAllText(Path.Combine(root, p)))).ToList();
                var change = Renamed(property.Name) is { } to ? $"se renombra a {to}" : "se elimina";

                if (usedIn.Count > 0)
                {
                    warnings.Add($"{entity.Name}.{property.Name} {change}, pero tu código la usa en:\n" +
                                 string.Join("\n", usedIn.Select(p => "      " + p)) + "\n    Actualízalo para que compile.");
                }

                if (Renamed(property.Name) is null && updated.Persistence is not null)
                {
                    warnings.Add($"{entity.Name}.{property.Name} se elimina: la próxima migración borrará su columna y los datos que tenga.");
                }
            }

            string? Renamed(string name) => renamedTo.TryGetValue(name, out var to) ? to : null;
        }

        return warnings;
    }

    public static ServiceDefinition Load(string directory)
    {
        var path = Path.Combine(directory, ServiceGenerator.DefinitionFileName);
        if (!File.Exists(path))
        {
            throw new StratumException(ServiceGenerator.IsLegacyService(directory)
                ? $"'{Path.GetFullPath(directory)}' es un servicio de una versión anterior (msg.json, paquetes Msg.*). Pásalo a Stratum antes de cambiarlo: stratum upgrade --path \"{directory}\""
                : $"No hay {ServiceGenerator.DefinitionFileName} en '{Path.GetFullPath(directory)}'. ¿Es un servicio generado con Stratum?");
        }

        return ServiceGenerator.Deserialize(File.ReadAllText(path));
    }

    private static bool IsCreateOnce(string path, string service) =>
        path.StartsWith($"src/{service}.Api/Features/", StringComparison.Ordinal)
        || path.StartsWith($"src/{service}.Api/Data/", StringComparison.Ordinal)
        || path.StartsWith($"src/{service}.Api/Events/", StringComparison.Ordinal)
        || path.StartsWith($"src/{service}.Domain/", StringComparison.Ordinal)
        || path.StartsWith($"src/{service}.Application/", StringComparison.Ordinal)
        || path.StartsWith($"src/{service}.Infrastructure/", StringComparison.Ordinal)
        || path.StartsWith($"tests/{service}.Api.Tests/Features/", StringComparison.Ordinal)
        || path.StartsWith($"tests/{service}.Api.Tests/Domain/", StringComparison.Ordinal)
        || path == $"tests/{service}.Api.Tests/ApiFactory.cs"
        || path == $"tests/{service}.Api.Tests/TestAuthHandler.cs"
        || path == "compose.yaml";

    /// <summary>Services generated before 0.3.0 don't call the generated registration: tell the user what to add.</summary>
    private static List<string> CheckWiring(string root, string service, FeatureSet features)
    {
        var warnings = new List<string>();
        var program = Path.Combine(root, "src", $"{service}.Api", "Program.cs");
        var code = File.Exists(program) ? File.ReadAllText(program) : string.Empty;

        var wired = code.Contains("AddStratumWiring()", StringComparison.Ordinal) || code.Contains("AddFeatures()", StringComparison.Ordinal);
        if (!wired || !code.Contains("MapFeatures()", StringComparison.Ordinal))
        {
            warnings.Add(
                $"src/{service}.Api/Program.cs no registra las entidades. Añade:\n" +
                $"      using {service}.Api.Features;\n" +
                "      builder.Services.AddValidation();\n" +
                "      builder.AddFeatures();       // después de AddValidatedOptions\n" +
                "      v1.MapFeatures();            // después de MapPingEndpoints");
        }
        else if (!code.Contains("AddStratumWiring()", StringComparison.Ordinal) && !code.Contains("AddValidation()", StringComparison.Ordinal))
        {
            warnings.Add($"src/{service}.Api/Program.cs: añade builder.Services.AddValidation(); para validar las peticiones.");
        }

        var defaultsTests = Path.Combine(root, "tests", $"{service}.Api.Tests", "ServiceDefaultsTests.cs");
        if (features.Tests && File.Exists(defaultsTests)
            && File.ReadAllText(defaultsTests).Contains("WebApplicationFactory<Program>", StringComparison.Ordinal))
        {
            warnings.Add(
                $"tests/{service}.Api.Tests/ServiceDefaultsTests.cs usa WebApplicationFactory<Program>: cámbialo por ApiFactory " +
                "para que los tests usen la base de datos SQLite en memoria.");
        }

        return warnings;
    }
}
