namespace Msg.Core.Model;

/// <summary>
/// Single source of truth for a generated service. It is serialized as <c>msg.json</c>
/// at the root of every generated service, so the CLI and the visual editor can
/// read it back later (regenerate, add entities, upgrade templates...).
/// </summary>
public sealed record ServiceDefinition
{
    public string MsgVersion { get; init; } = MsgInfo.Version;

    public required ServiceInfo Service { get; init; }

    public FeatureSet Features { get; init; } = new();

    /// <summary>Null until the first entity is added.</summary>
    public PersistenceSettings? Persistence { get; init; }

    public IReadOnlyList<EntityDefinition> Entities { get; init; } = [];
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

    public static IReadOnlyList<string> Names { get; } =
        ["observability", "openApi", "resilience", "auth", "tests", "docker", "ci"];

    public bool IsEnabled(string feature) => feature.ToUpperInvariant() switch
    {
        "OBSERVABILITY" => Observability,
        "OPENAPI" => OpenApi,
        "RESILIENCE" => Resilience,
        "AUTH" => Auth,
        "TESTS" => Tests,
        "DOCKER" => Docker,
        "CI" => Ci,
        _ => throw new MsgException($"Feature desconocida en plantilla: '{feature}'. Válidas: {string.Join(", ", Names)}."),
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
        _ => throw new MsgException($"Feature desconocida: '{feature}'. Válidas: {string.Join(", ", Names)}."),
    };
}
