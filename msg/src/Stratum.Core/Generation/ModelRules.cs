using System.Text.RegularExpressions;
using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>Rules between entities of a service: references (foreign keys) and aggregate operations.</summary>
public static partial class ModelRules
{
    private static readonly HashSet<string> ReservedOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Create", "Update", "Delete", "Get", "List", "Apply", "Raise", "EnsureInvariants", "Customize",
    };

    [GeneratedRegex("^[A-Z][A-Za-z0-9]{0,63}$")]
    private static partial Regex Pascal();

    /// <summary>Throws <see cref="StratumException"/> with the first broken rule.</summary>
    public static void Validate(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var names = definition.Entities.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        ValidateEnums(definition, names);
        ValidateTypes(definition, names);
        ValidatePublishedOperations(definition);

        foreach (var entity in definition.Entities)
        {
            foreach (var reference in entity.References)
            {
                if (!names.Contains(reference.References!))
                {
                    var similar = definition.Entities.FirstOrDefault(e => e.Name.Equals(reference.References, StringComparison.OrdinalIgnoreCase));
                    throw new StratumException(similar is not null
                        ? $"{entity.Name}.{reference.Name} apunta a '{reference.References}': ¿querías decir '{similar.Name}'?"
                        : $"{entity.Name}.{reference.Name} apunta a '{reference.References}', que no es una entidad ni un enumerado de este servicio " +
                          $"(entidades: {(names.Count == 0 ? "ninguna" : string.Join(", ", names))}; enumerados: {string.Join(", ", definition.EnumNames.DefaultIfEmpty("ninguno"))}). " +
                          "Para un enumerado, créalo antes (stratum enum add). Una entidad de otro servicio se guarda como guid y se mantiene al día con sus eventos.");
                }

                if (reference.References == entity.Name && !reference.Nullable)
                {
                    throw new StratumException($"{entity.Name}.{reference.Name} apunta a la propia entidad: tiene que ser opcional ({reference.Name}:{entity.Name}?), si no, el primer {entity.Name} no se podría crear.");
                }
            }

            ValidateOperations(definition, entity);
        }

        // Required references can't form a cycle: none of those rows could ever be created.
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entity in definition.Entities)
        {
            Visit(entity.Name, []);
        }

        void Visit(string name, List<string> path)
        {
            if (state.GetValueOrDefault(name) == 2)
            {
                return;
            }

            if (state.GetValueOrDefault(name) == 1)
            {
                var cycle = path.SkipWhile(n => n != name).Append(name);
                throw new StratumException($"Referencias obligatorias en círculo ({string.Join(" → ", cycle)}): haz opcional (?) al menos una.");
            }

            state[name] = 1;
            path.Add(name);
            var entity = definition.Entities.First(e => e.Name == name);
            foreach (var target in entity.References.Where(r => !r.Nullable && r.References != name).Select(r => r.References!).Distinct())
            {
                Visit(target, path);
            }

            path.RemoveAt(path.Count - 1);
            state[name] = 2;
        }
    }

    private static void ValidateEnums(ServiceDefinition definition, HashSet<string> entityNames)
    {
        var enums = definition.Enums ?? [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in enums)
        {
            if (!Pascal().IsMatch(e.Name) || PropertyTypes.All.Contains(e.Name.ToLowerInvariant()) || e.Name is "Enum" or "Guid" or "String")
            {
                throw new StratumException($"'{e.Name}' no vale como nombre de enumerado: PascalCase, p. ej. OrderStatus.");
            }

            if (!seen.Add(e.Name) || entityNames.Contains(e.Name))
            {
                throw new StratumException($"Ya hay una entidad o un enumerado que se llama '{e.Name}'.");
            }

            if (e.Values.Count == 0)
            {
                throw new StratumException($"El enumerado {e.Name} necesita al menos un valor.");
            }

            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in e.Values)
            {
                if (!Pascal().IsMatch(value))
                {
                    throw new StratumException($"'{value}' no vale como valor de {e.Name}: PascalCase, sin espacios (p. ej. InProgress).");
                }

                if (!values.Add(value))
                {
                    throw new StratumException($"{e.Name} repite el valor '{value}'.");
                }
            }
        }

        var known = enums.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var type in definition.Types ?? [])
        {
            foreach (var p in type.Properties.Where(p => p.Type == PropertyTypes.EnumType && (p.Enum is null || !known.Contains(p.Enum))))
            {
                throw new StratumException($"{type.Name}.{p.Name} usa el enumerado '{p.Enum}', que no existe en este servicio.");
            }
        }

        foreach (var entity in definition.Entities)
        {
            foreach (var p in entity.Properties.Concat((entity.Operations ?? []).SelectMany(o => o.Parameters)).Where(p => p.Type == PropertyTypes.EnumType))
            {
                if (p.Enum is null || !known.Contains(p.Enum))
                {
                    throw new StratumException($"{entity.Name}.{p.Name} usa el enumerado '{p.Enum}', que no existe en este servicio.");
                }
            }
        }
    }

    private static void ValidateTypes(ServiceDefinition definition, HashSet<string> entityNames)
    {
        var types = definition.Types ?? [];
        var enumNames = definition.EnumNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types)
        {
            if (!Pascal().IsMatch(type.Name) || PropertyTypes.All.Contains(type.Name.ToLowerInvariant()) || type.Name is "Object" or "Guid" or "String" or "List")
            {
                throw new StratumException($"'{type.Name}' no vale como nombre de tipo: PascalCase, p. ej. OrderItem.");
            }

            if (!seen.Add(type.Name) || entityNames.Contains(type.Name) || enumNames.Contains(type.Name))
            {
                throw new StratumException($"Ya hay una entidad, un enumerado o un tipo que se llama '{type.Name}'.");
            }

            if (type.Properties.Count == 0)
            {
                throw new StratumException($"El tipo {type.Name} necesita al menos una propiedad.");
            }

            foreach (var p in type.Properties)
            {
                if (p.IsList)
                {
                    throw new StratumException($"{type.Name}.{p.Name}: un tipo de elemento no puede tener listas dentro (una sola capa de anidamiento).");
                }

                if (p.References is not null)
                {
                    throw new StratumException($"{type.Name}.{p.Name}: un tipo de elemento no puede apuntar a otra entidad. Guarda el id como guid ({p.Name}:guid).");
                }

                if (p.Name is "Id")
                {
                    throw new StratumException($"{type.Name}: 'Id' lo usa Stratum para la fila de cada elemento. Usa otro nombre (p. ej. ProductId).");
                }
            }
        }

        var known = types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var entity in definition.Entities)
        {
            foreach (var p in entity.Properties.Where(p => p.IsObjectList))
            {
                if (p.Of is null || !known.Contains(p.Of))
                {
                    throw new StratumException($"{entity.Name}.{p.Name} usa el tipo '{p.Of}', que no existe en este servicio.");
                }

                var owner = $"{entity.Name}Id";
                if (types.First(t => t.Name == p.Of).Properties.Any(x => x.Name == owner))
                {
                    throw new StratumException($"{p.Of}.{owner}: ese nombre lo usa Stratum para enlazar cada elemento con su {entity.Name}.");
                }
            }

            foreach (var p in entity.Properties.Where(p => p.Type == PropertyTypes.ObjectType && !p.IsList))
            {
                throw new StratumException($"{entity.Name}.{p.Name}: un tipo de elemento solo puede ser una lista en una entidad ({p.Name}:{p.Of}[]).");
            }

            foreach (var o in entity.Operations ?? [])
            {
                foreach (var p in o.Parameters.Where(p => p.Type == PropertyTypes.ObjectType && (p.Of is null || !known.Contains(p.Of))))
                {
                    throw new StratumException($"{entity.Name}.{o.Name}({p.Name}) usa el tipo '{p.Of}', que no existe en este servicio.");
                }
            }
        }
    }

    private static void ValidatePublishedOperations(ServiceDefinition definition)
    {
        foreach (var entity in definition.Entities)
        {
            foreach (var o in (entity.Operations ?? []).Where(o => o.Publishes))
            {
                if (definition.Messaging is null)
                {
                    throw new StratumException($"Para publicar {entity.Name}.{o.Name} como evento de integración, activa la mensajería del servicio (stratum messaging on).");
                }

                var name = o.Event.StartsWith(entity.Name, StringComparison.Ordinal) ? o.Event[entity.Name.Length..] : string.Empty;
                if (name.Length == 0)
                {
                    throw new StratumException($"{entity.Name}.{o.Name}: para publicarlo, su evento tiene que empezar por {entity.Name} (ahora es {o.Event}; p. ej. {DefaultEvent(entity.Name, o.Name)}).");
                }

                if (Subscription.Events.Contains(name))
                {
                    throw new StratumException($"{entity.Name}.{o.Name}: el evento {o.Event} choca con el {name} que ya publica Stratum. Usa otro nombre de evento.");
                }

                if ((entity.Operations ?? []).Count(x => x.Publishes && x.Event == o.Event) > 1)
                {
                    throw new StratumException($"{entity.Name}: dos operaciones publicadas con el mismo evento {o.Event}.");
                }
            }
        }
    }

    /// <summary>Lists that use the item type: "Order.Items".</summary>
    public static IReadOnlyList<string> TypeUsages(ServiceDefinition definition, string typeName)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return [.. definition.Entities.SelectMany(e => e.Properties.Where(p => p.Of == typeName).Select(p => $"{e.Name}.{p.Name}")
            .Concat((e.Operations ?? []).SelectMany(o => o.Parameters.Where(p => p.Of == typeName).Select(p => $"{e.Name}.{o.Name}({p.Name})"))))];
    }

    /// <summary>Properties and operation parameters that use the enumeration: "Order.Status".</summary>
    public static IReadOnlyList<string> EnumUsages(ServiceDefinition definition, string enumName)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return [.. definition.Entities.SelectMany(e =>
            e.Properties.Where(p => p.Enum == enumName).Select(p => $"{e.Name}.{p.Name}")
                .Concat((e.Operations ?? []).SelectMany(o => o.Parameters.Where(p => p.Enum == enumName).Select(p => $"{e.Name}.{o.Name}({p.Name})")))),
            .. (definition.Types ?? []).SelectMany(t => t.Properties.Where(p => p.Enum == enumName).Select(p => $"{t.Name}.{p.Name}"))];
    }

    /// <summary>"Draft, Confirmed, Shipped" → values (PascalCase).</summary>
    public static IReadOnlyList<string> ParseEnumValues(string values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(values);
        return [.. values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.Length > 0 ? char.ToUpperInvariant(v[0]) + v[1..] : v)];
    }

    /// <summary>
    /// Entities whose properties point to <paramref name="entity"/>: "Order.CustomerId". With
    /// <paramref name="includeSelf"/>, also its own self-references (Order.ParentId), which block a delete too.
    /// </summary>
    public static IReadOnlyList<(EntityDefinition Entity, PropertyDefinition Property)> Dependents(ServiceDefinition definition, string entity, bool includeSelf = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return [.. definition.Entities
            .Where(e => includeSelf || e.Name != entity)
            .SelectMany(e => e.References.Where(r => r.References == entity).Select(r => (e, r)))];
    }

    /// <summary>"Confirm" + no event name → "OrderConfirmed". Parameters with the same rules as --props.</summary>
    /// <param name="types">Item types of the service: a parameter can be one item ("Item:OrderItem") or a list of them.</param>
    public static EntityOperation ParseOperation(string entity, string name, string? parameters, string? eventName, IReadOnlyCollection<string>? enums = null, IReadOnlyCollection<string>? types = null)
    {
        var operation = ServiceName.Normalize(name);
        return new EntityOperation
        {
            Name = operation,
            Parameters = string.IsNullOrWhiteSpace(parameters) ? [] : PropertyTypes.Parse(parameters, enums, types, singleItems: true),
            Event = string.IsNullOrWhiteSpace(eventName) ? DefaultEvent(entity, operation) : ServiceName.Normalize(eventName),
        };
    }

    /// <summary>
    /// Order + Confirm → OrderConfirmed; with an object after the verb, the verb goes last:
    /// Order + AddItem → OrderItemAdded, Order + ChangeAddress → OrderAddressChanged.
    /// </summary>
    public static string DefaultEvent(string entity, string operation)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        var split = 1;
        while (split < operation.Length && !char.IsUpper(operation[split]))
        {
            split++;
        }

        return entity + operation[split..] + PastTense(operation[..split]);
    }

    /// <summary>Good enough for event names: Confirm → Confirmed, Ship → Shipped, Pay → Paid, Cancel → Cancelled.</summary>
    public static string PastTense(string verb)
    {
        ArgumentException.ThrowIfNullOrEmpty(verb);
        var irregular = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Pay"] = "Paid", ["Send"] = "Sent", ["Buy"] = "Bought", ["Sell"] = "Sold", ["Cancel"] = "Cancelled",
            ["Begin"] = "Begun", ["Hold"] = "Held", ["Pick"] = "Picked", ["Fix"] = "Fixed", ["Lend"] = "Lent",
            ["Spend"] = "Spent", ["Build"] = "Built", ["Put"] = "Put", ["Set"] = "Set", ["Reset"] = "Reset",
            ["Freeze"] = "Frozen", ["Choose"] = "Chosen", ["Write"] = "Written", ["Give"] = "Given", ["Take"] = "Taken",
            ["Lock"] = "Locked", ["Unlock"] = "Unlocked", ["Submit"] = "Submitted", ["Refund"] = "Refunded",
            ["Add"] = "Added", ["Remove"] = "Removed", ["Replace"] = "Replaced", ["Change"] = "Changed", ["Clear"] = "Cleared",
        };
        if (irregular.TryGetValue(verb, out var past))
        {
            return past;
        }

        if (verb.EndsWith('e'))
        {
            return verb + "d";
        }

        if (verb.EndsWith('y') && verb.Length > 1 && !"aeiou".Contains(verb[^2], StringComparison.Ordinal))
        {
            return verb[..^1] + "ied";
        }

        // Ship → Shipped, Stop → Stopped (short consonant-vowel-consonant verbs).
        if (verb.Length is >= 3 and <= 4 && !"aeiouwxy".Contains(verb[^1], StringComparison.Ordinal)
            && "aeiou".Contains(verb[^2], StringComparison.Ordinal) && !"aeiou".Contains(verb[^3], StringComparison.Ordinal))
        {
            return verb + verb[^1] + "ed";
        }

        return verb + "ed";
    }

    private static void ValidateOperations(ServiceDefinition definition, EntityDefinition entity)
    {
        var operations = entity.Operations ?? [];
        if (operations.Count == 0)
        {
            return;
        }

        if (definition.Architecture != Architectures.CleanDdd)
        {
            throw new StratumException($"Las operaciones de agregado ({entity.Name}.{operations[0].Name}) son de la arquitectura clean-ddd. En vertical-slice añade tus endpoints en {entity.Name}Endpoints.cs.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var events = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            entity.Name + "Created", entity.Name + "Updated", entity.Name + "Deleted",
        };
        foreach (var operation in operations)
        {
            if (!Pascal().IsMatch(operation.Name) || ReservedOperations.Contains(operation.Name))
            {
                throw new StratumException($"'{operation.Name}' no vale como operación de {entity.Name}: un verbo en PascalCase que no sea {string.Join(", ", ReservedOperations)}.");
            }

            if (!seen.Add(operation.Name))
            {
                throw new StratumException($"{entity.Name} ya tiene la operación '{operation.Name}'.");
            }

            if (!Pascal().IsMatch(operation.Event) || !events.Add(operation.Event))
            {
                throw new StratumException($"El evento '{operation.Event}' de {entity.Name}.{operation.Name} no es válido o ya existe.");
            }

            if (entity.Properties.Any(p => p.Name == operation.Name))
            {
                throw new StratumException($"{entity.Name} tiene una propiedad '{operation.Name}': la operación necesita otro nombre.");
            }

            foreach (var parameter in operation.Parameters.Where(p => p.References is not null))
            {
                if (!definition.Entities.Any(e => e.Name == parameter.References))
                {
                    throw new StratumException($"{entity.Name}.{operation.Name}: el parámetro {parameter.Name} apunta a '{parameter.References}', que no es una entidad de este servicio.");
                }
            }
        }
    }
}
