using Msg.Core.Generation;
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

    /// <summary>Prefix of <see cref="Type"/> for a reference: "@Customer" is a foreign key to Customer.</summary>
    public const string ReferencePrefix = "@";

    public string? References => Type.StartsWith(ReferencePrefix, StringComparison.Ordinal) ? Type[ReferencePrefix.Length..] : null;

    public PropertyDefinition ToDefinition() => References is { } target
        ? new PropertyDefinition { Name = Name.Trim(), Type = "guid", Nullable = Nullable, References = target }
        : new PropertyDefinition { Name = Name.Trim(), Type = Type, Nullable = Nullable };

    public static PropertyDraft From(PropertyDefinition p) =>
        new() { Name = p.Name, Type = p.References is null ? p.Type : ReferencePrefix + p.References, Nullable = p.Nullable, OriginalName = p.Name };
}

/// <summary>A business operation of an aggregate being edited (clean-ddd).</summary>
public sealed class OperationDraft
{
    public string Name { get; set; } = string.Empty;

    /// <summary>"Reason:string, Amount:decimal" (same format as --props), may be empty.</summary>
    public string Parameters { get; set; } = string.Empty;

    /// <summary>Domain event; empty → entity + past tense of the name.</summary>
    public string Event { get; set; } = string.Empty;

    public static OperationDraft From(EntityOperation o) =>
        new() { Name = o.Name, Parameters = PropertyTypes.Spec(o.Parameters), Event = o.Event };
}

/// <summary>
/// Editable entity. Existing entities with the editable layout (Stratum 0.10+) can change their properties or be removed;
/// older ones must be converted first.
/// </summary>
public sealed class EntityDraft
{
    public string Name { get; set; } = string.Empty;

    public string Plural { get; set; } = string.Empty;

    public bool IsNew { get; init; }

    /// <summary>False for entities created before Stratum 0.10 (until <see cref="Convert"/> is set).</summary>
    public bool IsLegacy { get; init; }

    /// <summary>Marked to be removed when the changes are applied.</summary>
    public bool Removed { get; set; }

    /// <summary>Legacy entity marked to be converted to the editable layout.</summary>
    public bool Convert { get; set; }

    public bool CanEdit => !Removed && (IsNew || !IsLegacy || Convert);

    /// <summary>Publish {Entity}Created/Updated/Deleted integration events (needs messaging in the service).</summary>
    public bool PublishEvents { get; set; }

    public List<PropertyDraft> Properties { get; } = [];

    public List<OperationDraft> Operations { get; } = [];

    /// <summary>Operations as msg.json defines them (throws <see cref="MsgException"/> on invalid parameters).</summary>
    public IReadOnlyList<EntityOperation>? ToOperations() => Operations.Count == 0 ? null :
        [.. Operations
            .Where(o => !string.IsNullOrWhiteSpace(o.Name))
            .Select(o => ModelRules.ParseOperation(Name, o.Name, o.Parameters, o.Event))];

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
            .Select(p => p.ToDefinition())],
    };

    public static EntityDraft From(EntityDefinition entity, bool isNew = false)
    {
        var draft = new EntityDraft { Name = entity.Name, Plural = entity.Plural, IsNew = isNew, IsLegacy = !isNew && !entity.IsEditable, PublishEvents = entity.Publishes };
        draft.Properties.AddRange(entity.Properties.Select(PropertyDraft.From));
        draft.Operations.AddRange((entity.Operations ?? []).Select(OperationDraft.From));
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

    /// <summary>Program.cs delegates to MsgWiring.g.cs (Stratum 0.12+): features can be turned on and off.</summary>
    public bool UsesWiring { get; set; }

    /// <summary>Program.cs from before 0.12 marked to be rewritten to use MsgWiring.g.cs.</summary>
    public bool ConvertWiring { get; set; }

    public bool CanEditFeatures => IsNew || UsesWiring || ConvertWiring;

    /// <summary>Broker for integration events (MessagingSettings.Kafka / InMemory), or null: no messaging.</summary>
    public string? Broker { get; set; }

    /// <summary>Events of other services this one listens to.</summary>
    public List<Subscription> Subscriptions { get; } = [];

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
            Broker = definition.Messaging?.Broker,
            MsgVersion = definition.MsgVersion,
        };
        draft.Entities.AddRange(definition.Entities.Select(e => EntityDraft.From(e)));
        draft.Subscriptions.AddRange(definition.Subscriptions ?? []);
        return draft;
    }
}
