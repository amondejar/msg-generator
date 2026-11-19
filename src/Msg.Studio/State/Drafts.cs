using Msg.Core.Model;

namespace Msg.Studio.State;

/// <summary>Editable copy of a property while it is being designed in the Studio.</summary>
public sealed class PropertyDraft
{
    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = "string";

    public bool Nullable { get; set; }

    public static PropertyDraft From(PropertyDefinition p) => new() { Name = p.Name, Type = p.Type, Nullable = p.Nullable };
}

/// <summary>Editable entity. Entities that already exist in the service are shown read-only (their code is the user's).</summary>
public sealed class EntityDraft
{
    public string Name { get; set; } = string.Empty;

    public string Plural { get; set; } = string.Empty;

    public bool IsNew { get; init; }

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
        var draft = new EntityDraft { Name = entity.Name, Plural = entity.Plural, IsNew = false };
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
