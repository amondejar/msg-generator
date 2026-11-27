using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>
/// One edit made in the entities designer. Kind: "entity" (new entity: <see cref="Entity"/> with
/// <see cref="Properties"/> as in --props), "relation" (Entity.Property → Target), "updateRelation",
/// "composition" (Entity.Property is one or a list of <see cref="Target"/>, an item type) or "remove" (a relation or a
/// composition goes away).
/// </summary>
public sealed record EntityChange(
    string Kind,
    string Entity,
    string? Property = null,
    string? Target = null,
    bool OneToOne = false,
    bool Optional = false,
    string? OnDelete = null,
    bool List = false,
    string? Properties = null,
    bool Many = false)
{
    public string Describe() => Kind switch
    {
        "entity" => $"Nueva entidad {Entity} ({Properties})",
        "relation" => $"{Entity}.{Property} → {Target} ({(Many ? "N–M" : OneToOne ? "1–1" : "1–N")}{(Optional ? ", opcional" : string.Empty)})",
        "updateRelation" => $"{Entity}.{Property}: {(OneToOne ? "1–1" : "1–N")}{(Optional ? ", opcional" : string.Empty)}, al borrar {OnDelete}",
        "composition" => $"{Entity}.{Property} ◆ {Target}{(List ? "[]" : string.Empty)}",
        "remove" => $"Quitar {Entity}.{Property}",
        _ => $"{Kind} {Entity}.{Property}",
    };
}

/// <summary>What saving the entities designer does to the service, and the changes that can't be done (with why).</summary>
public sealed record EntityDesignerPlan(ChangePlan? Plan, IReadOnlyList<string> Errors, ServiceDefinition Updated);

/// <summary>
/// The entities designer: a batch of entities, relations and compositions of one service, planned (and applied)
/// together with the same rules as "stratum relation" and "stratum add entity".
/// </summary>
public static class EntityDesigner
{
    public static EntityDesignerPlan Plan(string directory, ServiceDefinition current, IReadOnlyList<EntityChange> changes, GenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changes);
        var updated = current;
        var errors = new List<string>();
        foreach (var change in changes)
        {
            try
            {
                updated = change.Kind switch
                {
                    "entity" => AddEntity(updated, change),
                    "relation" => Relations.Add(updated, change.Entity, change.Property, Required(change.Target, "la entidad destino"),
                        change.OneToOne, change.Optional, change.OnDelete, change.Many),
                    "updateRelation" => Relations.Update(updated, change.Entity, Required(change.Property, "la propiedad"),
                        change.OneToOne, change.Optional, change.OnDelete ?? DeleteBehaviors.Restrict),
                    "composition" => Relations.AddComposition(updated, change.Entity, Required(change.Property, "la propiedad"),
                        Required(change.Target, "el tipo de elemento"), change.List, change.Optional),
                    "remove" => Relations.Remove(updated, change.Entity, Required(change.Property, "la propiedad")),
                    _ => throw new StratumException($"Cambio desconocido: {change.Kind}."),
                };
            }
            catch (StratumException e)
            {
                errors.Add($"{change.Describe()}: {e.Message}");
            }
        }

        if (errors.Count > 0 || ReferenceEquals(updated, current))
        {
            return new EntityDesignerPlan(errors.Count > 0 ? null : new ChangePlan(directory, [], []), errors, updated);
        }

        try
        {
            return new EntityDesignerPlan(ServicePlanner.Plan(directory, current, updated, options), errors, updated);
        }
        catch (StratumException e)
        {
            return new EntityDesignerPlan(null, [e.Message], updated);
        }
    }

    private static ServiceDefinition AddEntity(ServiceDefinition definition, EntityChange change)
    {
        var name = ServiceName.Normalize(change.Entity);
        var properties = PropertyTypes.Parse(Required(change.Properties, "las propiedades"), definition.EnumNames, definition.TypeNames);
        var entity = ServicePlanner.ValidateEntity(new EntityDefinition
        {
            Name = name,
            Plural = PropertyTypes.Pluralize(name),
            Properties = properties,
            Layout = EntityDefinition.CurrentLayout,
        }, definition);
        var updated = definition with
        {
            Entities = [.. definition.Entities, entity],
            Persistence = definition.Persistence ?? new PersistenceSettings { Provider = PersistenceSettings.Postgres },
        };
        ModelRules.Validate(updated);
        return updated;
    }

    private static string Required(string? value, string what) =>
        string.IsNullOrWhiteSpace(value) ? throw new StratumException($"Falta {what}.") : value.Trim();
}
