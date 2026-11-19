using Msg.Core.Model;

namespace Msg.Studio.State;

/// <summary>Editable copy of a property while it is being designed in the Studio.</summary>
public sealed class PropertyDraft
{
    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = "string";

    public bool Nullable { get; set; }

    /// <summary>Name in msg.json. Null for a property added in this session. If it differs from <see cref="Name"/>, it's a rename.</summary>
    public string? OriginalName { get; init; }

    public bool IsRenamed => OriginalName is not null && !OriginalName.Equals(Name.Trim(), StringComparison.Ordinal);

    public static PropertyDraft From(PropertyDefinition p) =>
        new() { Name = p.Name, Type = p.Type, Nullable = p.Nullable, OriginalName = p.Name };
}

/// <summary>
/// Editable entity. Existing entities with the editable layout (MSG 0.10+) can change their properties or be removed;
/// older ones must be converted first.
/// </summary>
public sealed class EntityDraft
{
    public string Name { get; set; } = string.Empty;

    public string Plural { get; set; } = string.Empty;

    public bool IsNew { get; init; }

    /// <summary>False for entities created before MSG 0.10 (until <see cref="Convert"/> is set).</summary>
    public bool IsLegacy { get; init; }

    /// <summary>Marked to be removed when the changes are applied.</summary>
    public bool Removed { get; set; }

    /// <summary>Legacy entity marked to be converted to the editable layout.</summary>
    public bool Convert { get; set; }

    public bool CanEdit => !Removed && (IsNew || !IsLegacy || Convert);

    public List<PropertyDraft> Properties { get; } = [];

    public string EffectivePlural =>
        !string.IsNullOrWhiteSpace(Plural) ? Plural
        : string.IsNullOrWhiteSpace(Name) ? string.Empty
        : PropertyTypes.Pluralize(Name);

    public EntityDefinition ToDefinition() => new()
    {
        Name = Name,
        Plural = Plural,
        Properties = [.. Properties
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new PropertyDefinition { Name = p.Name.Trim(), Type = p.Type, Nullable = p.Nullable })],
    };

    public static EntityDraft From(EntityDefinition entity)
    {
        var draft = new EntityDraft { Name = entity.Name, Plural = entity.Plural, IsNew = false, IsLegacy = !entity.IsEditable };
        draft.Properties.AddRange(entity.Properties.Select(PropertyDraft.From));
        return draft;
    }
}

/// <summary>The service being edited: a new one (everything editable) or an existing one (add entities).</summary>
public sealed class ServiceDraft
{
    public required string Directory { get; init; }

    public required bool IsNew { get; init; }

    public string Name { get; set; } = string.Empty;

    public string Architecture { get; set; } = Architectures.VerticalSlice;

    public FeatureSet Features { get; set; } = new();

    /// <summary>Chosen database. Locked once the service has persistence.</summary>
    public string Provider { get; set; } = PersistenceSettings.Postgres;

    public bool ProviderLocked { get; init; }

    public string MsgVersion { get; init; } = Msg.Core.MsgInfo.Version;

    public List<EntityDraft> Entities { get; } = [];

    /// <summary>When removing entities, also delete the user's files (handlers, repositories...).</summary>
    public bool DeleteOwnFiles { get; set; }

    public bool CanEditStructure => IsNew;

    public static ServiceDraft ForExisting(string directory, ServiceDefinition definition)
    {
        var draft = new ServiceDraft
        {
            Directory = directory,
            IsNew = false,
            Name = definition.Service.Name,
            Architecture = definition.Architecture,
            Features = definition.Features,
            Provider = definition.Persistence?.Provider ?? PersistenceSettings.Postgres,
            ProviderLocked = definition.Persistence is not null,
            MsgVersion = definition.MsgVersion,
        };
        draft.Entities.AddRange(definition.Entities.Select(EntityDraft.From));
        return draft;
    }
}
