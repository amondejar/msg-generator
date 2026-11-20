using System.Text.RegularExpressions;
using Msg.Core.Model;

namespace Msg.Core.Generation;

/// <summary>Rules between entities of a service: references (foreign keys) and aggregate operations.</summary>
public static partial class ModelRules
{
    private static readonly HashSet<string> ReservedOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Create", "Update", "Delete", "Get", "List", "Apply", "Raise", "EnsureInvariants", "Customize",
    };

    [GeneratedRegex("^[A-Z][A-Za-z0-9]{0,63}$")]
    private static partial Regex Pascal();

    /// <summary>Throws <see cref="MsgException"/> with the first broken rule.</summary>
    public static void Validate(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var names = definition.Entities.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var entity in definition.Entities)
        {
            foreach (var reference in entity.References)
            {
                if (!names.Contains(reference.References!))
                {
                    var similar = definition.Entities.FirstOrDefault(e => e.Name.Equals(reference.References, StringComparison.OrdinalIgnoreCase));
                    throw new MsgException(similar is not null
                        ? $"{entity.Name}.{reference.Name} apunta a '{reference.References}': ¿querías decir '{similar.Name}'?"
                        : $"{entity.Name}.{reference.Name} apunta a '{reference.References}', que no es una entidad de este servicio " +
                          $"({(names.Count == 0 ? "no hay entidades" : string.Join(", ", names))}). Una entidad de otro servicio se guarda como guid " +
                          "(sin clave ajena) y se mantiene al día con sus eventos.");
                }

                if (reference.References == entity.Name && !reference.Nullable)
                {
                    throw new MsgException($"{entity.Name}.{reference.Name} apunta a la propia entidad: tiene que ser opcional ({reference.Name}:{entity.Name}?), si no, el primer {entity.Name} no se podría crear.");
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
                throw new MsgException($"Referencias obligatorias en círculo ({string.Join(" → ", cycle)}): haz opcional (?) al menos una.");
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
    public static EntityOperation ParseOperation(string entity, string name, string? parameters, string? eventName)
    {
        var operation = ServiceName.Normalize(name);
        return new EntityOperation
        {
            Name = operation,
            Parameters = string.IsNullOrWhiteSpace(parameters) ? [] : PropertyTypes.Parse(parameters),
            Event = string.IsNullOrWhiteSpace(eventName) ? entity + PastTense(operation) : ServiceName.Normalize(eventName),
        };
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
            throw new MsgException($"Las operaciones de agregado ({entity.Name}.{operations[0].Name}) son de la arquitectura clean-ddd. En vertical-slice añade tus endpoints en {entity.Name}Endpoints.cs.");
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
                throw new MsgException($"'{operation.Name}' no vale como operación de {entity.Name}: un verbo en PascalCase que no sea {string.Join(", ", ReservedOperations)}.");
            }

            if (!seen.Add(operation.Name))
            {
                throw new MsgException($"{entity.Name} ya tiene la operación '{operation.Name}'.");
            }

            if (!Pascal().IsMatch(operation.Event) || !events.Add(operation.Event))
            {
                throw new MsgException($"El evento '{operation.Event}' de {entity.Name}.{operation.Name} no es válido o ya existe.");
            }

            if (entity.Properties.Any(p => p.Name == operation.Name))
            {
                throw new MsgException($"{entity.Name} tiene una propiedad '{operation.Name}': la operación necesita otro nombre.");
            }

            foreach (var parameter in operation.Parameters.Where(p => p.References is not null))
            {
                if (!definition.Entities.Any(e => e.Name == parameter.References))
                {
                    throw new MsgException($"{entity.Name}.{operation.Name}: el parámetro {parameter.Name} apunta a '{parameter.References}', que no es una entidad de este servicio.");
                }
            }
        }
    }
}
