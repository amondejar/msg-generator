using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>
/// A relation between two entities of the same service: <see cref="Entity"/>.<see cref="Property"/> points to
/// <see cref="Target"/> (Address.CustomerId → Customer). 1–N by default; 1–1 when <see cref="OneToOne"/>.
/// </summary>
public sealed record Relation(string Entity, string Property, string Target, bool OneToOne, bool Optional, string OnDelete)
{
    /// <summary>"1–N" or "1–1" (or "0..1–N" style is left to the UI).</summary>
    public string Kind => OneToOne ? "1-1" : "1-N";
}

/// <summary>An item type inside an entity: one (ShippingAddress:Address) or a list (Lines:OrderLine[]).</summary>
public sealed record Composition(string Entity, string Property, string Type, bool List, bool Optional);

/// <summary>
/// Relations between the entities of a service, as the CLI ("stratum relation"), the entities designer and the MCP
/// server change them. They live in stratum.json as properties: a reference (references, unique, onDelete) or an item
/// type (of, list). EF Core gets them from there (foreign keys, unique index, delete behavior, owned types).
/// </summary>
public static class Relations
{
    public static IReadOnlyList<Relation> Of(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return [.. definition.Entities.SelectMany(e => e.References.Select(p =>
            new Relation(e.Name, p.Name, p.References!, p.IsOneToOne, p.Nullable, p.DeleteBehavior)))];
    }

    public static IReadOnlyList<Composition> CompositionsOf(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return [.. definition.Entities.SelectMany(e => e.Properties.Where(p => p.Type == PropertyTypes.ObjectType).Select(p =>
            new Composition(e.Name, p.Name, p.Of!, p.IsList, p.Nullable)))];
    }

    /// <summary>"Customer" → "CustomerId"; "Owner" → "OwnerId".</summary>
    public static string DefaultProperty(string target) => target + "Id";

    /// <summary>Entity.Property points to Target (a new property of Entity).</summary>
    public static ServiceDefinition Add(ServiceDefinition definition, string entity, string? property, string target,
        bool oneToOne = false, bool optional = false, string? onDelete = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var owner = ServicePlanner.FindEntity(definition, entity);
        var parent = ServicePlanner.FindEntity(definition, target);
        var name = Normalize(property ?? DefaultProperty(parent.Name));
        if (owner.Properties.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new StratumException($"{owner.Name} ya tiene una propiedad {name}. Elige otro nombre o cambia esa relación (stratum relation update {owner.Name}.{name}).");
        }

        var reference = new PropertyDefinition
        {
            Name = name,
            Type = "guid",
            Nullable = optional,
            References = parent.Name,
            Unique = oneToOne ? true : null,
            OnDelete = Behavior(onDelete),
        };
        return Replace(definition, owner, [.. owner.Properties, reference]);
    }

    /// <summary>Changes the kind (1–1 / 1–N), whether it is optional, or what deleting the target does.</summary>
    public static ServiceDefinition Update(ServiceDefinition definition, string entity, string property,
        bool? oneToOne = null, bool? optional = null, string? onDelete = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var owner = ServicePlanner.FindEntity(definition, entity);
        var current = FindReference(owner, property);
        var updated = current with
        {
            Unique = (oneToOne ?? current.IsOneToOne) ? true : null,
            Nullable = optional ?? current.Nullable,
            OnDelete = onDelete is null ? current.OnDelete : Behavior(onDelete),
        };

        if (updated.DeleteBehavior == DeleteBehaviors.SetNull && !updated.Nullable)
        {
            updated = updated with { OnDelete = onDelete is null ? null : updated.OnDelete };   // required now: back to restrict
        }

        return Replace(definition, owner, [.. owner.Properties.Select(p => p == current ? updated : p)]);
    }

    /// <summary>Removes the relation: the property (and its column) goes away.</summary>
    public static ServiceDefinition Remove(ServiceDefinition definition, string entity, string property)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var owner = ServicePlanner.FindEntity(definition, entity);
        var current = owner.Properties.FirstOrDefault(p => p.Name.Equals(property, StringComparison.OrdinalIgnoreCase) && (p.References is not null || p.Type == PropertyTypes.ObjectType))
            ?? throw new StratumException($"{owner.Name}.{property} no es una relación ni una composición.");
        if (owner.Properties.Count == 1)
        {
            throw new StratumException($"{owner.Name} se quedaría sin propiedades: una entidad necesita al menos una.");
        }

        return Replace(definition, owner, [.. owner.Properties.Where(p => p != current)]);
    }

    /// <summary>An item type inside the entity: one (its columns in the same table) or a list (its own table).</summary>
    public static ServiceDefinition AddComposition(ServiceDefinition definition, string entity, string property, string type, bool list, bool optional = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var owner = ServicePlanner.FindEntity(definition, entity);
        var item = (definition.Types ?? []).FirstOrDefault(t => t.Name.Equals(type, StringComparison.OrdinalIgnoreCase))
            ?? throw new StratumException($"No hay ningún tipo de elemento '{type}'. Créalo antes (stratum type add {type} --props \"...\").");
        var name = Normalize(property);
        if (owner.Properties.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new StratumException($"{owner.Name} ya tiene una propiedad {name}.");
        }

        var composition = new PropertyDefinition
        {
            Name = name,
            Type = PropertyTypes.ObjectType,
            Of = item.Name,
            List = list ? true : null,
            Nullable = !list && optional,
        };
        return Replace(definition, owner, [.. owner.Properties, composition]);
    }

    /// <summary>
    /// What a change of relations means for the data already in the database: the plan shows it before applying,
    /// and the migration review again when it is created.
    /// </summary>
    public static IReadOnlyList<string> Warnings(ServiceDefinition current, ServiceDefinition updated)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(updated);
        var warnings = new List<string>();
        if (updated.Persistence?.Provider == PersistenceSettings.SqlServer)
        {
            foreach (var entity in updated.Entities)
            {
                foreach (var group in entity.References.Where(r => r.DeleteBehavior != DeleteBehaviors.Restrict).GroupBy(r => r.References).Where(g => g.Count() > 1))
                {
                    warnings.Add($"SQL Server no admite dos caminos en cascada hasta {entity.Name} desde {group.Key} ({string.Join(", ", group.Select(r => r.Name))}): la migración fallará al aplicarse. Deja solo uno en cascada o vaciando y el resto en «impedir».");
                }
            }
        }

        foreach (var entity in updated.Entities)
        {
            var before = current.Entities.FirstOrDefault(e => e.Name == entity.Name);
            if (before is null)
            {
                continue;   // a new table: nothing in it yet
            }

            foreach (var reference in entity.References)
            {
                var old = before.Properties.FirstOrDefault(p => p.Name == reference.Name);
                if (!reference.Nullable && (old is null || old.Nullable))
                {
                    warnings.Add($"{entity.Name}.{reference.Name} es una relación obligatoria nueva: si la tabla de {entity.Plural} ya tiene filas, no tienen {reference.References} y la migración fallará. Créala opcional, rellena los datos y hazla obligatoria después.");
                }

                if (reference.IsOneToOne && old is not null && !old.IsOneToOne)
                {
                    warnings.Add($"{entity.Name}.{reference.Name} pasa a 1–1: si ya hay dos {entity.Plural} con el mismo {reference.References}, el índice único de la migración fallará.");
                }

                if (reference.DeleteBehavior == DeleteBehaviors.Cascade && old?.DeleteBehavior != DeleteBehaviors.Cascade)
                {
                    warnings.Add($"{entity.Name}.{reference.Name}: al borrar un {reference.References} se borrarán también sus {entity.Plural} (en cascada, en la base de datos).");
                }
            }

            foreach (var item in entity.Properties.Where(p => p.IsObject && !p.Nullable))
            {
                if (before.Properties.All(p => p.Name != item.Name))
                {
                    warnings.Add($"{entity.Name}.{item.Name} es obligatorio y nuevo: las filas que ya existan tendrán sus columnas vacías (valores por defecto). Revisa la migración.");
                }
            }
        }

        return warnings;
    }

    private static PropertyDefinition FindReference(EntityDefinition owner, string property) =>
        owner.Properties.FirstOrDefault(p => p.Name.Equals(property, StringComparison.OrdinalIgnoreCase) && p.References is not null)
        ?? throw new StratumException($"{owner.Name}.{property} no es una relación (relaciones de {owner.Name}: {string.Join(", ", owner.References.Select(r => r.Name).DefaultIfEmpty("ninguna"))}).");

    private static string? Behavior(string? onDelete)
    {
        if (onDelete is null)
        {
            return null;
        }

        var behavior = DeleteBehaviors.Normalize(onDelete)
            ?? throw new StratumException($"'{onDelete}' no es un comportamiento al borrar. Usa restrict (impedir), cascade (borrar también) o set-null (vaciar).");
        return behavior == DeleteBehaviors.Restrict ? null : behavior;
    }

    private static string Normalize(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? throw new StratumException("Falta el nombre de la propiedad.") : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private static ServiceDefinition Replace(ServiceDefinition definition, EntityDefinition owner, IReadOnlyList<PropertyDefinition> properties)
    {
        ServicePlanner.RequireEditable(owner);
        var updated = definition with { Entities = [.. definition.Entities.Select(e => e == owner ? e with { Properties = properties } : e)] };
        ModelRules.Validate(updated);
        return updated;
    }
}
