namespace Msg.Core.Model;

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

    /// <summary>Publishing service (its name in its msg.json, e.g. "Orders").</summary>
    public required string Service { get; init; }

    public required string Entity { get; init; }

    /// <summary>Created, Updated or Deleted.</summary>
    public required string Event { get; init; }

    /// <summary>Data of the event besides Id (empty for Deleted).</summary>
    public IReadOnlyList<PropertyDefinition> Properties { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public string EventClass => Entity + Event;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Topic => TopicFor(Service, Entity, Event);

    public static string TopicFor(string service, string entity, string eventName) =>
        $"{ServiceName.ToKebab(service)}.{Generation.EntityCodeGen.Kebab(entity)}-{eventName.ToLowerInvariant()}";

    public bool Matches(Subscription other) =>
        other is not null && other.Service == Service && other.Entity == Entity && other.Event == Event;
}

/// <summary>Integration events between services (Msg.Messaging). Null in msg.json: no messaging.</summary>
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
        ?? throw new MsgException($"Broker desconocido '{broker}'. Válidos: {string.Join(", ", Brokers)}.");
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
    /// Needs messaging in the service. Null = false (keeps older msg.json files unchanged).
    /// </summary>
    public bool? PublishEvents { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Publishes => PublishEvents == true;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEditable => Layout >= CurrentLayout;
}

public sealed record PropertyDefinition
{
    public required string Name { get; init; }

    /// <summary>One of <see cref="PropertyTypes.All"/> (msg names, not C# names).</summary>
    public required string Type { get; init; }

    public bool Nullable { get; init; }
}
