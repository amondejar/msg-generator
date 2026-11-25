namespace Stratum.Core.Model;

/// <summary>
/// Single source of truth for a generated service. It is serialized as <c>stratum.json</c>
/// at the root of every generated service, so the CLI and the visual editor can
/// read it back later (regenerate, add entities, upgrade templates...).
/// </summary>
public sealed record ServiceDefinition
{
    public string StratumVersion { get; init; } = StratumInfo.Version;

    public required ServiceInfo Service { get; init; }

    /// <summary><see cref="Architectures.VerticalSlice"/> (default) or <see cref="Architectures.CleanDdd"/>.</summary>
    public string Architecture { get; init; } = Architectures.VerticalSlice;

    public FeatureSet Features { get; init; } = new();

    /// <summary>Null until the first entity is added.</summary>
    public PersistenceSettings? Persistence { get; init; }

    public IReadOnlyList<EntityDefinition> Entities { get; init; } = [];

    /// <summary>Integration events between services. Null: the service doesn't use messaging.</summary>
    public MessagingSettings? Messaging { get; init; }

    /// <summary>Events of other services this one reacts to. Null when there are none.</summary>
    public IReadOnlyList<Subscription>? Subscriptions { get; init; }

    /// <summary>
    /// Local copies of entities of other services, kept up to date with their Created/Updated/Deleted events
    /// ("stratum replica add"). Null when there are none.
    /// </summary>
    public IReadOnlyList<ReplicaDefinition>? Replicas { get; init; }

    /// <summary>Enumerations entities can use as property types.</summary>
    public IReadOnlyList<EnumDefinition>? Enums { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> EnumNames => [.. (Enums ?? []).Select(e => e.Name)];

    /// <summary>
    /// Types of the items of a list property ("Items:OrderItem[]"): part of the entity that owns the list, stored in
    /// a table of their own and replaced with the list.
    /// </summary>
    public IReadOnlyList<ItemTypeDefinition>? Types { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> TypeNames => [.. (Types ?? []).Select(t => t.Name)];

    /// <summary>
    /// Property renames not yet captured by a migration. Without them EF Core would drop the column and
    /// create a new one (losing data); Stratum turns them into <c>RenameColumn</c>. Null when there are none.
    /// </summary>
    public IReadOnlyList<PropertyRename>? PendingRenames { get; init; }
}

/// <summary>A property renamed in Stratum (Studio or "stratum entity update --rename").</summary>
public sealed record PropertyRename
{
    public required string Entity { get; init; }

    public required string From { get; init; }

    public required string To { get; init; }
}

public static class Architectures
{
    /// <summary>One project organized by feature; endpoints use EF Core directly.</summary>
    public const string VerticalSlice = "vertical-slice";

    /// <summary>Domain / Application / Infrastructure / Api, aggregates with domain events, use-case handlers.</summary>
    public const string CleanDdd = "clean-ddd";

    public static IReadOnlyList<string> All { get; } = [VerticalSlice, CleanDdd];

    public static string Validate(string value) =>
        All.Contains(value)
            ? value
            : throw new StratumException($"Arquitectura '{value}' no soportada. Opciones: {string.Join(", ", All)}.");
}

public sealed record ServiceInfo
{
    /// <summary>PascalCase service name, e.g. <c>Orders</c>. Used for namespaces and project names.</summary>
    public required string Name { get; init; }
}

/// <summary>
/// Cross-cutting concerns. The non-negotiable baseline (project layout, health checks,
/// ProblemDetails, correlation id, structured logging, validated options) is always generated;
/// these flags only cover the parts a team may reasonably opt out of.
/// </summary>
public sealed record FeatureSet
{
    /// <summary>OpenTelemetry traces, metrics and logs with OTLP export.</summary>
    public bool Observability { get; init; } = true;

    /// <summary>OpenAPI document generated from the endpoints.</summary>
    public bool OpenApi { get; init; } = true;

    /// <summary>Standard resilience pipeline (retry, timeout, circuit breaker) for every HttpClient.</summary>
    public bool Resilience { get; init; } = true;

    /// <summary>JWT bearer authentication with an "authenticated by default" fallback policy.</summary>
    public bool Auth { get; init; }

    /// <summary>Integration tests with WebApplicationFactory.</summary>
    public bool Tests { get; init; } = true;

    /// <summary>Multi-stage Dockerfile running as non-root.</summary>
    public bool Docker { get; init; } = true;

    /// <summary>GitHub Actions pipeline.</summary>
    public bool Ci { get; init; } = true;

    /// <summary>MCP server at /mcp: the entities' CRUD and operations as tools for AI assistants.</summary>
    public bool Mcp { get; init; }

    public static IReadOnlyList<string> Names { get; } =
        ["observability", "openApi", "resilience", "auth", "tests", "docker", "ci", "mcp"];

    public bool IsEnabled(string feature) => feature.ToUpperInvariant() switch
    {
        "OBSERVABILITY" => Observability,
        "OPENAPI" => OpenApi,
        "RESILIENCE" => Resilience,
        "AUTH" => Auth,
        "TESTS" => Tests,
        "DOCKER" => Docker,
        "CI" => Ci,
        "MCP" => Mcp,
        _ => throw new StratumException($"Feature desconocida en plantilla: '{feature}'. Válidas: {string.Join(", ", Names)}."),
    };

    public FeatureSet With(string feature, bool enabled) => feature.ToUpperInvariant() switch
    {
        "OBSERVABILITY" => this with { Observability = enabled },
        "OPENAPI" => this with { OpenApi = enabled },
        "RESILIENCE" => this with { Resilience = enabled },
        "AUTH" => this with { Auth = enabled },
        "TESTS" => this with { Tests = enabled },
        "DOCKER" => this with { Docker = enabled },
        "CI" => this with { Ci = enabled },
        "MCP" => this with { Mcp = enabled },
        _ => throw new StratumException($"Feature desconocida: '{feature}'. Válidas: {string.Join(", ", Names)}."),
    };
}
