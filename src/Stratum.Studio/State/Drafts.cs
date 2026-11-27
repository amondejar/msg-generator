using Stratum.Core.Generation;
using Stratum.Core.Model;

namespace Stratum.Studio.State;

/// <summary>Editable copy of a property while it is being designed in the Studio.</summary>
public sealed class PropertyDraft
{
    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = "string";

    public bool Nullable { get; set; }

    /// <summary>A list ("Tags:string[]", "Lines:OrderLine[]"); an item type without it is one item inside the entity.</summary>
    public bool List { get; set; }

    /// <summary>Only for a reference: 1–1 (one row per target) instead of 1–N.</summary>
    public bool OneToOne { get; set; }

    /// <summary>Only for a reference: what deleting the target does (<see cref="DeleteBehaviors"/>).</summary>
    public string OnDelete { get; set; } = DeleteBehaviors.Restrict;

    /// <summary>Validation (length, format, pattern, range); only the rules that fit the type are kept.</summary>
    public PropertyRules? Rules { get; set; }

    /// <summary>The rules that apply to a property of this type (text rules for texts, the range for numbers).</summary>
    public static PropertyRules? Fitting(PropertyRules? rules, string type, bool list)
    {
        if (rules is null || list)
        {
            return null;
        }

        return type switch
        {
            "string" => (rules with { Min = null, Max = null }).OrNull(),
            "int" or "long" or "decimal" => (rules with { MinLength = null, MaxLength = null, Format = null, Pattern = null, Example = null }).OrNull(),
            _ => null,
        };
    }

    /// <summary>Name in stratum.json. Null for a property added in this session. If it differs from <see cref="Name"/>, it's a rename.</summary>
    public string? OriginalName { get; init; }

    public bool IsRenamed => OriginalName is not null && !OriginalName.Equals(Name.Trim(), StringComparison.Ordinal);

    /// <summary>Prefix of <see cref="Type"/> for a reference: "@Customer" is a foreign key to Customer.</summary>
    public const string ReferencePrefix = "@";

    /// <summary>Prefix of <see cref="Type"/> for an enumeration: "#OrderStatus".</summary>
    public const string EnumPrefix = "#";

    public string? References => Type.StartsWith(ReferencePrefix, StringComparison.Ordinal) ? Type[ReferencePrefix.Length..] : null;

    public string? Enum => Type.StartsWith(EnumPrefix, StringComparison.Ordinal) ? Type[EnumPrefix.Length..] : null;

    /// <summary>Prefix of <see cref="Type"/> for a list of an item type: "*OrderItem" is "Items:OrderItem[]".</summary>
    public const string ItemPrefix = "*";

    public string? Item => Type.StartsWith(ItemPrefix, StringComparison.Ordinal) ? Type[ItemPrefix.Length..] : null;

    /// <summary>A list; with a reference, an N–M relation ("TagIds:Tag[]").</summary>
    public bool IsList => List;

    public PropertyDefinition ToDefinition()
    {
        var name = Name.Trim();
        var list = IsList ? true : (bool?)null;
        var nullable = Nullable && !IsList;
        return References is { } target
            ? IsList
                ? new PropertyDefinition
                {
                    // N–M: Tags → TagIds, like --props "Tags:Tag[]".
                    Name = PropertyTypes.Parse($"{(name.Length == 0 ? target : name)}:{target}[]").Single().Name, Type = "guid", References = target, List = true,
                    OnDelete = OnDelete == DeleteBehaviors.Cascade ? DeleteBehaviors.Cascade : DeleteBehaviors.Restrict,
                }
                : new PropertyDefinition
                {
                    Name = name, Type = "guid", Nullable = nullable, References = target,
                    Unique = OneToOne, OnDelete = OnDelete == DeleteBehaviors.SetNull && !nullable ? DeleteBehaviors.Restrict : OnDelete,
                }
            : Item is { } item
                ? new PropertyDefinition { Name = name, Type = PropertyTypes.ObjectType, List = list, Nullable = nullable, Of = item }
                : Enum is { } enumName
                    ? new PropertyDefinition { Name = name, Type = PropertyTypes.EnumType, Nullable = nullable, Enum = enumName, List = list }
                    : new PropertyDefinition { Name = name, Type = Type, Nullable = nullable, List = list, Rules = Fitting(Rules, Type, IsList) };
    }

    public static PropertyDraft From(PropertyDefinition p) => new()
    {
        Name = p.Name,
        Type = p.References is not null ? ReferencePrefix + p.References
            : p.Of is not null ? ItemPrefix + p.Of
            : p.Enum is not null ? EnumPrefix + p.Enum : p.Type,
        Nullable = p.Nullable,
        List = p.IsList,
        OneToOne = p.IsOneToOne,
        OnDelete = p.DeleteBehavior,
        Rules = p.Rules,
        OriginalName = p.Name,
    };
}

/// <summary>An enumeration being edited.</summary>
public sealed class EnumDraft
{
    public string Name { get; set; } = string.Empty;

    /// <summary>"Draft, Confirmed, Shipped".</summary>
    public string Values { get; set; } = string.Empty;

    /// <summary>Name in stratum.json (null when new): renaming an enumeration in use is not supported.</summary>
    public string? OriginalName { get; init; }

    public static EnumDraft From(EnumDefinition e) => new() { Name = e.Name, Values = string.Join(", ", e.Values), OriginalName = e.Name };
}

/// <summary>An item type (of the lists of objects) being edited.</summary>
public sealed class TypeDraft
{
    public string Name { get; set; } = string.Empty;

    /// <summary>"ProductId:guid, Quantity:int" (same format as --props).</summary>
    public string Properties { get; set; } = string.Empty;

    /// <summary>Name in stratum.json (null when new): renaming a type in use is not supported.</summary>
    public string? OriginalName { get; init; }

    /// <summary>Fields as stratum.json had them: their validation rules (set with "stratum rule") aren't in <see cref="Properties"/>.</summary>
    public IReadOnlyList<PropertyDefinition> OriginalFields { get; init; } = [];

    public static TypeDraft From(ItemTypeDefinition t) => new() { Name = t.Name, Properties = PropertyTypes.Spec(t.Properties), OriginalName = t.Name, OriginalFields = t.Properties };
}

/// <summary>A business operation of an aggregate being edited (clean-ddd).</summary>
public sealed class OperationDraft
{
    public string Name { get; set; } = string.Empty;

    /// <summary>"Reason:string, Amount:decimal" (same format as --props), may be empty.</summary>
    public string Parameters { get; set; } = string.Empty;

    /// <summary>Domain event; empty → entity + past tense of the name.</summary>
    public string Event { get; set; } = string.Empty;

    /// <summary>Also an integration event for other services (OrderShipped).</summary>
    public bool Publish { get; set; }

    public static OperationDraft From(EntityOperation o) =>
        new() { Name = o.Name, Parameters = PropertyTypes.Spec(o.Parameters), Event = o.Event, Publish = o.Publishes };
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

    /// <summary>Operations as stratum.json defines them (throws <see cref="StratumException"/> on invalid parameters).</summary>
    public IReadOnlyList<EntityOperation>? ToOperations(IReadOnlyCollection<string>? enums = null, IReadOnlyCollection<string>? types = null) => Operations.Count == 0 ? null :
        [.. Operations
            .Where(o => !string.IsNullOrWhiteSpace(o.Name))
            .Select(o => ModelRules.ParseOperation(Name, o.Name, o.Parameters, o.Event, enums, types) with { Publish = o.Publish ? true : null })];

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

    public string StratumVersion { get; init; } = Stratum.Core.StratumInfo.Version;

    public List<EntityDraft> Entities { get; } = [];

    public List<EnumDraft> Enums { get; } = [];

    /// <summary>Enumerations as stratum.json defines them (throws <see cref="StratumException"/> on invalid values).</summary>
    public IReadOnlyList<EnumDefinition>? ToEnums() => Enums.Count == 0 ? null :
        [.. Enums.Where(e => !string.IsNullOrWhiteSpace(e.Name)).Select(e => new EnumDefinition
        {
            Name = ServiceName.Normalize(e.Name),
            Values = string.IsNullOrWhiteSpace(e.Values) ? [] : ModelRules.ParseEnumValues(e.Values),
        })];

    public List<TypeDraft> Types { get; } = [];

    /// <summary>Item types as stratum.json defines them (throws <see cref="StratumException"/> on invalid properties).</summary>
    public IReadOnlyList<ItemTypeDefinition>? ToTypes(IReadOnlyCollection<string> enums) => Types.Count == 0 ? null :
        [.. Types.Where(t => !string.IsNullOrWhiteSpace(t.Name)).Select(t => new ItemTypeDefinition
        {
            Name = ServiceName.Normalize(t.Name),
            Properties = string.IsNullOrWhiteSpace(t.Properties) ? [] : [.. PropertyTypes.Parse(t.Properties, enums).Select(p =>
                t.OriginalFields.FirstOrDefault(o => o.Name == p.Name && o.Type == p.Type && o.IsList == p.IsList) is { Rules: { } rules } ? p with { Rules = rules } : p)],
        })];

    /// <summary>When removing entities, also delete the user's files (handlers, repositories...).</summary>
    public bool DeleteOwnFiles { get; set; }

    public bool CanEditStructure => IsNew;

    /// <summary>Program.cs delegates to StratumWiring.g.cs (Stratum 0.12+): features can be turned on and off.</summary>
    public bool UsesWiring { get; set; }

    /// <summary>Program.cs from before 0.12 marked to be rewritten to use StratumWiring.g.cs.</summary>
    public bool ConvertWiring { get; set; }

    public bool CanEditFeatures => IsNew || UsesWiring || ConvertWiring;

    /// <summary>Broker for integration events (MessagingSettings.Kafka / InMemory), or null: no messaging.</summary>
    public string? Broker { get; set; }

    /// <summary>
    /// Set when the Studio turned messaging on by itself (to publish or listen to an event), so the pages can say so.
    /// </summary>
    public string? BrokerNote { get; set; }

    /// <summary>Events of other services this one listens to (with a handler of yours).</summary>
    public List<Subscription> Subscriptions { get; } = [];

    /// <summary>Local copies of entities of other services (see <see cref="Stratum.Core.Generation.Replicas"/>).</summary>
    public List<ReplicaDefinition> Replicas { get; } = [];

    /// <summary>The Created/Updated/Deleted subscriptions the replicas need (Handler = false).</summary>
    public List<Subscription> ReplicaSubscriptions { get; } = [];

    /// <summary>
    /// The subscriptions of the service: yours, plus the ones only the replicas use (in the order of
    /// <paramref name="original"/> when nothing changed, so nothing is rewritten).
    /// </summary>
    public List<Subscription> AllSubscriptions(IReadOnlyList<Subscription>? original)
    {
        var all = Subscriptions.ToList();
        foreach (var s in ReplicaSubscriptions.Where(s => Replicas.Any(r => r.Service == s.Service && r.Entity == s.Entity)))
        {
            if (!all.Any(x => x.Topic == s.Topic))
            {
                all.Add(s);
            }
        }

        return original is not null && original.Count == all.Count && original.All(all.Contains) ? [.. original] : all;
    }

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
            StratumVersion = definition.StratumVersion,
        };
        draft.Entities.AddRange(definition.Entities.Select(e => EntityDraft.From(e)));
        draft.Enums.AddRange((definition.Enums ?? []).Select(EnumDraft.From));
        draft.Types.AddRange((definition.Types ?? []).Select(TypeDraft.From));
        draft.Subscriptions.AddRange((definition.Subscriptions ?? []).Where(s => s.HasHandler));
        draft.Replicas.AddRange(definition.Replicas ?? []);
        draft.ReplicaSubscriptions.AddRange((definition.Subscriptions ?? [])
            .Where(s => draft.Replicas.Any(r => r.Service == s.Service && r.Entity == s.Entity) && Subscription.Events.Contains(s.Event))
            .Select(s => s.HasHandler ? s with { Handler = false } : s));
        return draft;
    }
}
