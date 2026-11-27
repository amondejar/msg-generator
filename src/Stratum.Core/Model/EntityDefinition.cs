namespace Stratum.Core.Model;

/// <summary>Database used by the service. Set the first time an entity is added.</summary>
public sealed record PersistenceSettings
{
    public const string Postgres = "postgres";
    public const string SqlServer = "sqlserver";
    public static IReadOnlyList<string> Providers { get; } = [Postgres, SqlServer];

    public required string Provider { get; init; }
}

/// <summary>
/// An event of another service this service reacts to. The event's data is copied here (the contract), so the
/// service can be generated without the publisher at hand; "stratum subscribe --sync" refreshes it.
/// </summary>
public sealed record Subscription
{
    public const string Created = "Created";
    public const string Updated = "Updated";
    public const string Deleted = "Deleted";

    public static IReadOnlyList<string> Events { get; } = [Created, Updated, Deleted];

    /// <summary>Publishing service (its name in its stratum.json, e.g. "Orders").</summary>
    public required string Service { get; init; }

    public required string Entity { get; init; }

    /// <summary>Created, Updated or Deleted.</summary>
    public required string Event { get; init; }

    /// <summary>Data of the event besides Id (empty for Deleted).</summary>
    public IReadOnlyList<PropertyDefinition> Properties { get; init; } = [];

    /// <summary>
    /// False: no handler of yours for this event (a replica uses it, with its own generated handler). Null = true.
    /// </summary>
    public bool? Handler { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasHandler => Handler != false;

    [System.Text.Json.Serialization.JsonIgnore]
    public string EventClass => Entity + Event;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Topic => TopicFor(Service, Entity, Event);

    public static string TopicFor(string service, string entity, string eventName) =>
        $"{ServiceName.ToKebab(service)}.{Generation.EntityCodeGen.Kebab(entity)}-{Generation.EntityCodeGen.Kebab(eventName)}";

    public bool Matches(Subscription other) =>
        other is not null && other.Service == Service && other.Entity == Entity && other.Event == Event;
}

/// <summary>Integration events between services (Stratum.Messaging). Null in stratum.json: no messaging.</summary>
public sealed record MessagingSettings
{
    public const string Kafka = "kafka";

    /// <summary>Azure Service Bus (with Aspire, its local emulator).</summary>
    public const string AzureServiceBus = "azureServiceBus";

    /// <summary>In-process broker: one service alone, or tests. Events don't reach other services.</summary>
    public const string InMemory = "inMemory";

    public static IReadOnlyList<string> Brokers { get; } = [Kafka, AzureServiceBus, InMemory];

    public required string Broker { get; init; }

    public static string Validate(string broker) =>
        Brokers.FirstOrDefault(b => b.Equals(broker?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new StratumException($"Broker desconocido '{broker}'. Válidos: {string.Join(", ", Brokers)}.");
}

public sealed record EntityDefinition
{
    /// <summary>PascalCase singular, e.g. <c>Product</c>.</summary>
    public required string Name { get; init; }

    /// <summary>PascalCase plural, e.g. <c>Products</c>. Used for the folder, DbSet and route.</summary>
    public required string Plural { get; init; }

    public required IReadOnlyList<PropertyDefinition> Properties { get; init; }

    /// <summary>
    /// File layout of the entity's code. Null (entities created before Stratum 0.10): the files are the user's
    /// and Stratum can't edit the entity until it is converted. <see cref="CurrentLayout"/>: properties, contracts,
    /// mapping and CRUD live in <c>*.g.cs</c> files that Stratum regenerates; the user's code lives in partial classes.
    /// </summary>
    public int? Layout { get; init; }

    public const int CurrentLayout = 2;

    /// <summary>
    /// Publish {Entity}Created / Updated / Deleted integration events (with the entity's data) for other services.
    /// Needs messaging in the service. Null = false (keeps older stratum.json files unchanged).
    /// </summary>
    public bool? PublishEvents { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Publishes => PublishEvents == true && (PublishedEvents is null || PublishedEvents.Count > 0);

    /// <summary>
    /// Which of Created / Updated / Deleted it publishes, when not all three (e.g. ["Created"]). Null = the three
    /// (as before this option existed).
    /// </summary>
    public IReadOnlyList<string>? PublishedEvents { get; init; }

    /// <summary>Whether it publishes {Entity}{eventName} (Created, Updated or Deleted).</summary>
    public bool PublishesEvent(string eventName) =>
        PublishEvents == true && (PublishedEvents is null || PublishedEvents.Contains(eventName, StringComparer.OrdinalIgnoreCase));

    /// <summary>Business operations of the aggregate (clean-ddd only).</summary>
    public IReadOnlyList<EntityOperation>? Operations { get; init; }

    /// <summary>References to one row of another entity (1–N, 1–1): a foreign key column.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<PropertyDefinition> References => Properties.Where(p => p.References is not null && !p.IsList);

    /// <summary>N–M relations: a list of ids of another entity ("TagIds:Tag[]").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<PropertyDefinition> Links => Properties.Where(p => p.IsLink);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEditable => Layout >= CurrentLayout;
}

public sealed record PropertyDefinition
{
    public required string Name { get; init; }

    /// <summary>One of <see cref="PropertyTypes.All"/> (msg names, not C# names).</summary>
    public required string Type { get; init; }

    public bool Nullable { get; init; }

    /// <summary>
    /// Entity of the same service this property points to (a foreign key): "CustomerId:Customer" in --props.
    /// Type is then "guid". Null for plain properties.
    /// </summary>
    public string? References { get; init; }

    /// <summary>
    /// Only for a reference: at most one row may point to the same target (a 1–1 relation, e.g. Profile.CustomerId:
    /// a customer has one profile). A unique index in the database. Null = false (1–N).
    /// </summary>
    public bool? Unique { get; init; }

    /// <summary>
    /// Only for a reference: what deleting the target does to this row (<see cref="DeleteBehaviors"/>): restrict
    /// (409, the default: null), cascade (deleted too) or setNull (the reference is emptied; only optional ones).
    /// </summary>
    public string? OnDelete { get; init; }

    /// <summary>Enumeration of the service this property uses ("Status:OrderStatus"); Type is then "enum".</summary>
    public string? Enum { get; init; }

    /// <summary>
    /// A list: "Tags:string[]" (Type string), "Statuses:OrderStatus[]" (Type enum) or "Items:OrderItem[]" (Type
    /// object, <see cref="Of"/> OrderItem). Never null in the code: no items is an empty list. Null = false.
    /// </summary>
    public bool? List { get; init; }

    /// <summary>Item type of the service (<see cref="ServiceDefinition.Types"/>) of an "object" list.</summary>
    public string? Of { get; init; }

    /// <summary>
    /// Only in the contracts of integration events (subscriptions): the fields of the items of an "object" list, so
    /// the subscriber doesn't need the publisher's item type.
    /// </summary>
    public IReadOnlyList<PropertyDefinition>? Fields { get; init; }

    /// <summary>
    /// Validation of the value (texts: length, format, pattern; numbers: range), checked by the API (400), by the
    /// domain (clean-ddd) and, for the length, by the database. Null: only required/optional and 200 characters.
    /// </summary>
    public PropertyRules? Rules { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsList => List == true;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsObjectList => IsList && Type == PropertyTypes.ObjectType;

    /// <summary>One item of an item type inside the entity ("ShippingAddress:Address"): its columns in the same table.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsObject => !IsList && Type == PropertyTypes.ObjectType;

    /// <summary>An N–M relation: a list of ids of another entity of the service ("TagIds:Tag[]").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLink => References is not null && IsList;

    /// <summary>A reference that allows one row per target (1–1).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsOneToOne => References is not null && Unique == true;

    /// <summary>What deleting the target of this reference does (restrict when not set).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DeleteBehavior => OnDelete ?? DeleteBehaviors.Restrict;
}

/// <summary>What happens to the rows that point to an entity when it is deleted.</summary>
public static class DeleteBehaviors
{
    /// <summary>The delete is refused while rows point to it (409 with who uses it).</summary>
    public const string Restrict = "restrict";

    /// <summary>The rows that point to it are deleted too (in the database); in an N–M relation, only the link goes.</summary>
    public const string Cascade = "cascade";

    /// <summary>The reference of the rows that point to it is emptied (optional references only).</summary>
    public const string SetNull = "setNull";

    public static readonly string[] All = [Restrict, Cascade, SetNull];

    /// <summary>"cascade", "set-null", "SetNull"… → the stored name, or null when it isn't one.</summary>
    public static string? Normalize(string? value) => value?.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "restrict" => Restrict,
        "cascade" => Cascade,
        "setnull" or "null" => SetNull,
        _ => null,
    };
}

/// <summary>
/// The type of the items of a list ("Items:OrderItem[]"), e.g. OrderItem: ProductId, Quantity, UnitPrice. Its properties
/// are basic types or enumerations (no references, no lists).
/// </summary>
public sealed record ItemTypeDefinition
{
    public required string Name { get; init; }

    public required IReadOnlyList<PropertyDefinition> Properties { get; init; }
}

/// <summary>
/// An enumeration of the service (e.g. OrderStatus: Draft, Confirmed, Shipped). Stored as text in the database and in
/// the JSON of the API, so values can be added or reordered without touching existing data.
/// </summary>
public sealed record EnumDefinition
{
    public required string Name { get; init; }

    public required IReadOnlyList<string> Values { get; init; }
}

/// <summary>
/// A business operation of an aggregate (clean-ddd), e.g. Order.Confirm(reason): Stratum generates the domain event,
/// the command, its handler and POST /orders/{id}/confirm; the method body (the rule) is the user's.
/// </summary>
public sealed record EntityOperation
{
    /// <summary>Verb in PascalCase: "Confirm", "Cancel", "Ship".</summary>
    public required string Name { get; init; }

    /// <summary>Data the operation needs (none for a plain "Confirm").</summary>
    public IReadOnlyList<PropertyDefinition> Parameters { get; init; } = [];

    /// <summary>Domain event raised by the operation (e.g. "OrderConfirmed"). Defaults to entity + past tense.</summary>
    public required string Event { get; init; }

    /// <summary>
    /// Also an integration event for other services, with its own topic (OrderShipped → orders.order-shipped): the id,
    /// the data of the entity after the operation and the operation's parameters. Null = false.
    /// </summary>
    public bool? Publish { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Publishes => Publish == true;
}

/// <summary>
/// A local copy of an entity of another service (CustomerReplica in Addresses): its table, kept up to date by a
/// handler Stratum generates for the Created, Updated and Deleted events of the publisher. Read it to check that a
/// customer exists, or to show its name, without calling the other service.
/// </summary>
public sealed record ReplicaDefinition
{
    /// <summary>Publishing service (its name in its stratum.json, e.g. "Customers").</summary>
    public required string Service { get; init; }

    /// <summary>Entity of the publisher (e.g. "Customer").</summary>
    public required string Entity { get; init; }

    /// <summary>Data copied, as the events carry it (a subset of the Created event; Id always).</summary>
    public IReadOnlyList<PropertyDefinition> Properties { get; init; } = [];

    /// <summary>"CustomerReplica": the class and the table (replica_customers).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Name => Entity + "Replica";

    [System.Text.Json.Serialization.JsonIgnore]
    public string Plural => Entity + "Replicas";
}
