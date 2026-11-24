using System.Text.Json;
using Stratum.Core.Model;
using Stratum.Core.Templating;

namespace Stratum.Core.Generation;

public sealed record GeneratedFile(string Path, string Content);

/// <summary>Machine-specific generation settings. Not stored in stratum.json.</summary>
public sealed record GenerationOptions
{
    /// <summary>
    /// Folder with locally packed Stratum.* packages (before they are published to nuget.org).
    /// When set, a nuget.config with package source mapping is generated: Stratum.* comes only
    /// from this folder, everything else only from nuget.org.
    /// </summary>
    public string? LocalPackageSource { get; init; }

    /// <summary>The team's own templates and tokens (<c>.msg</c> folder), if any.</summary>
    public TeamTemplates? Team { get; init; }
}

/// <summary>
/// Turns a <see cref="ServiceDefinition"/> into files. It is pure (no disk I/O) on purpose:
/// the CLI writes the result to a folder, and the visual editor will be able to zip it,
/// preview it or push it to a repo using exactly the same code.
/// </summary>
public static class ServiceGenerator
{
    public const string TemplateName = "service";
    public const string EntityTemplateName = "entity";
    public const string CleanEntityTemplateName = "entity-clean";
    public const string EntityTestsTemplateName = "entity-tests";
    public const string SubscriptionTemplateName = "subscription";
    public const string OperationTemplateName = "operation";
    public const string DefinitionFileName = "stratum.json";

    /// <summary>Name before Stratum 0.22. Still read; "stratum upgrade" renames it.</summary>
    public const string LegacyDefinitionFileName = "msg.json";

    /// <summary>The service's definition file (stratum.json, or msg.json in services not migrated yet), or null.</summary>
    public static string? FindDefinition(string serviceDirectory)
    {
        foreach (var name in new[] { DefinitionFileName, LegacyDefinitionFileName })
        {
            var path = Path.Combine(serviceDirectory, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>A service generated before Stratum 0.22 (msg.json and Msg.* packages): "stratum upgrade" migrates it.</summary>
    public static bool IsLegacyService(string serviceDirectory) =>
        !File.Exists(Path.Combine(serviceDirectory, DefinitionFileName)) && File.Exists(Path.Combine(serviceDirectory, LegacyDefinitionFileName));

    // Whole files/folders that only exist when a feature is enabled.
    // Paths are template paths (before renaming), compared by prefix.
    private static readonly (string Prefix, string Feature)[] ConditionalPaths =
    [
        ("tests/", "tests"),
        ("tests/__Service__.Api.Tests/TestAuthHandler.cs", "auth"),
        ("Dockerfile", "docker"),
        ("_dockerignore", "docker"),
        ("_github/", "ci"),
        ("compose.yaml", "persistence"),
        ("src/__Service__.Api/Data/", "persistence"),
        ("src/__Service__.Api/Data/", "verticalSlice"),
        ("src/__Service__.Domain/", "cleanDdd"),
        ("src/__Service__.Application/", "cleanDdd"),
        ("src/__Service__.Infrastructure/", "cleanDdd"),
        ("src/__Service__.Infrastructure/Persistence/", "persistence"),
        ("src/__Service__.Api/Features/PagedResponse.cs", "persistence"),
    ];

    public static IReadOnlyList<GeneratedFile> Generate(ServiceDefinition definition, GenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ServiceName.Validate(definition.Service.Name);

        // Files always come from the current templates, so the service is stamped with the current Stratum version.
        definition = definition with { StratumVersion = StratumInfo.Version };

        var team = options?.Team;
        var tokens = BuildTokens(definition);
        foreach (var (key, value) in team?.Tokens ?? new Dictionary<string, string>())
        {
            tokens.TryAdd(key, value);   // Stratum's own tokens ({{Service}}...) can't be redefined
        }

        var isEnabled = Conditions(definition);
        var result = new List<GeneratedFile>();

        RenderTemplateSet(TemplateName, tokens, isEnabled, result, team);

        foreach (var entity in definition.Entities.Where(e => e.IsEditable))
        {
            RenderEntity(definition, entity, tokens, isEnabled, result, team);
        }

        if (definition.Messaging is not null)
        {
            foreach (var subscription in definition.Subscriptions ?? [])
            {
                var subscriptionTokens = new Dictionary<string, string>(tokens, StringComparer.Ordinal)
                {
                    ["Publisher"] = subscription.Service,
                    ["Entity"] = subscription.Entity,
                    ["EventClass"] = subscription.EventClass,
                    ["Topic"] = subscription.Topic,
                    ["EventParameters"] = string.Join(", ", ["Guid Id", .. subscription.Properties.Select(p => $"{PropertyTypes.ToCSharp(p, subscription.EventClass)} {p.Name}")]),
                    ["EventBody"] = EntityCodeGen.ContractBody(subscription.Properties),
                };
                RenderTemplateSet(SubscriptionTemplateName, subscriptionTokens, isEnabled, result, team);
            }
        }

        result.Add(new GeneratedFile(DefinitionFileName, Serialize(definition)));

        if (!string.IsNullOrWhiteSpace(options?.LocalPackageSource))
        {
            result.Add(new GeneratedFile("nuget.config", LocalNuGetConfig(options.LocalPackageSource)));
        }

        return result;
    }

    /// <summary>Files of one entity only (same content as in <see cref="Generate"/>).</summary>
    public static IReadOnlyList<GeneratedFile> GenerateEntity(ServiceDefinition definition, EntityDefinition entity, GenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(entity);

        var tokens = BuildTokens(definition with { StratumVersion = StratumInfo.Version });
        foreach (var (key, value) in options?.Team?.Tokens ?? new Dictionary<string, string>())
        {
            tokens.TryAdd(key, value);
        }

        var result = new List<GeneratedFile>();
        RenderEntity(definition, entity, tokens, Conditions(definition), result, options?.Team);
        return result;
    }

    private static void RenderEntity(
        ServiceDefinition definition,
        EntityDefinition entity,
        Dictionary<string, string> tokens,
        Func<string, bool> isEnabled,
        List<GeneratedFile> result,
        TeamTemplates? team)
    {
        var entityTokens = new Dictionary<string, string>(tokens, StringComparer.Ordinal);
        foreach (var (key, value) in EntityCodeGen.Tokens(entity, definition.Features, definition))
        {
            entityTokens[key] = value;
        }

        foreach (var (key, value) in EntityCodeGen.RelationTokens(definition, entity))
        {
            entityTokens[key] = value;
        }

        var hasReferences = entity.References.Any();
        var referenced = ModelRules.Dependents(definition, entity.Name, includeSelf: true, blockingOnly: true).Count > 0;
        var operations = definition.Architecture == Architectures.CleanDdd ? entity.Operations ?? [] : [];

        bool EntityConditions(string feature) => feature switch
        {
            "hasRequiredString" => EntityCodeGen.HasRequiredString(entity),
            "publishEvents" => entity.Publishes && definition.Messaging is not null,
            "publishCreated" => entity.PublishesEvent(Subscription.Created) && definition.Messaging is not null,
            "publishUpdated" => entity.PublishesEvent(Subscription.Updated) && definition.Messaging is not null,
            "publishDeleted" => entity.PublishesEvent(Subscription.Deleted) && definition.Messaging is not null,
            "integrationEvents" => IntegrationEvents.Of(definition, entity).Count > 0,
            "hasReferences" => hasReferences,
            "hasRequiredReferences" => entity.References.Any(p => !p.Nullable),
            "referenced" => referenced,
            "hasUnique" => entity.References.Any(p => p.IsOneToOne),
            "relations" => hasReferences || referenced,
            "hasOperations" => operations.Count > 0,
            "hasLists" => entity.Properties.Any(p => p.IsList),
            _ => isEnabled(feature),
        };

        var architectureTemplates = definition.Architecture == Architectures.CleanDdd
            ? CleanEntityTemplateName
            : EntityTemplateName;

        RenderTemplateSet(architectureTemplates, entityTokens, EntityConditions, result, team);
        RenderTemplateSet(EntityTestsTemplateName, entityTokens, EntityConditions, result, team);

        foreach (var operation in operations)
        {
            var operationTokens = new Dictionary<string, string>(entityTokens, StringComparer.Ordinal);
            foreach (var (key, value) in EntityCodeGen.OperationTokens(entity, operation))
            {
                operationTokens[key] = value;
            }

            RenderTemplateSet(OperationTemplateName, operationTokens, EntityConditions, result, team);
        }
    }

    /// <summary>
    /// Files that "stratum add entity" created before Stratum 0.10 (layout without .g.cs). They are the user's;
    /// Stratum only touches them when the user converts the entity to the editable layout.
    /// </summary>
    public static IReadOnlyList<string> LegacyEntityPaths(ServiceDefinition definition, EntityDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(entity);

        var s = definition.Service.Name;
        var (e, p) = (entity.Name, entity.Plural);
        string[] tests = [$"tests/{s}.Api.Tests/Features/{e}EndpointsTests.cs"];

        if (definition.Architecture != Architectures.CleanDdd)
        {
            return [
                $"src/{s}.Api/Features/{p}/{e}.cs",
                $"src/{s}.Api/Features/{p}/{e}Configuration.cs",
                $"src/{s}.Api/Features/{p}/{e}Contracts.cs",
                $"src/{s}.Api/Features/{p}/{e}Endpoints.cs",
                .. tests,
            ];
        }

        return [
            $"src/{s}.Domain/{p}/{e}.cs",
            $"src/{s}.Domain/{p}/{e}Events.cs",
            $"src/{s}.Domain/{p}/I{e}Repository.cs",
            $"src/{s}.Application/{p}/{e}Commands.cs",
            $"src/{s}.Application/{p}/{e}Queries.cs",
            $"src/{s}.Application/{p}/{e}EventHandlers.cs",
            $"src/{s}.Infrastructure/Persistence/{p}/{e}Configuration.cs",
            $"src/{s}.Infrastructure/Persistence/{p}/{e}Repository.cs",
            $"src/{s}.Api/Features/{p}/{e}Contracts.cs",
            $"src/{s}.Api/Features/{p}/{e}Endpoints.cs",
            $"tests/{s}.Api.Tests/Domain/{e}Tests.cs",
            .. tests,
        ];
    }

    internal static string LocalNuGetConfig(string source)
    {
        var path = System.Security.SecurityElement.Escape(Path.GetFullPath(source));
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <!--
              Generated by "stratum new" with a local package source. Uses locally packed StratumKit.* packages until they are
              published to nuget.org. Package source mapping: StratumKit.* ONLY from the local folder, everything
              else ONLY from nuget.org (protects against dependency confusion). Delete this file once
              StratumKit.* packages are available on your feed.
            -->
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                <add key="msg-local" value="{path}" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
                <packageSource key="msg-local">
                  <package pattern="StratumKit.*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>

            """;
    }

    /// <summary>Tokens Stratum defines itself; a team's config.json can't redefine them.</summary>
    public static IReadOnlySet<string> ReservedTokens { get; } = BuildReservedTokens();

    private static HashSet<string> BuildReservedTokens()
    {
        var sample = new ServiceDefinition { Service = new ServiceInfo { Name = "Sample" } };
        var entity = new EntityDefinition { Name = "Item", Plural = "Items", Properties = [new PropertyDefinition { Name = "Name", Type = "string" }] };
        return
        [
            .. BuildTokens(sample).Keys,
            .. EntityCodeGen.Tokens(entity, new FeatureSet()).Keys,
            .. EntityCodeGen.RelationTokens(sample with { Entities = [entity] }, entity).Keys,
            .. EntityCodeGen.OperationTokens(entity, new EntityOperation { Name = "Do", Event = "ItemDone" }).Keys,
        ];
    }

    public static Dictionary<string, string> BuildTokens(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var name = definition.Service.Name;

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Service"] = name,
            ["service-kebab"] = ServiceName.ToKebab(name),
            ["DevPort"] = ServiceName.DevPort(name).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["StratumVersion"] = StratumInfo.Version,
        };

        foreach (var (key, value) in EntityCodeGen.ServiceTokens(definition))
        {
            tokens[key] = value;
        }

        return tokens;
    }

    /// <summary>
    /// Template conditions: the user features plus derived ones (persistence, postgres, sqlserver)
    /// that come from the rest of stratum.json.
    /// </summary>
    public static Func<string, bool> Conditions(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var provider = definition.Persistence?.Provider;
        var clean = Architectures.Validate(definition.Architecture) == Architectures.CleanDdd;

        return feature => feature switch
        {
            "cleanDdd" => clean,
            "verticalSlice" => !clean,
            "persistence" => provider is not null,
            "postgres" => provider == PersistenceSettings.Postgres,
            "sqlserver" => provider == PersistenceSettings.SqlServer,
            "messaging" => definition.Messaging is not null,
            "connectionStrings" => provider is not null || definition.Messaging?.Broker is MessagingSettings.Kafka or MessagingSettings.AzureServiceBus,
            "kafka" => definition.Messaging?.Broker == MessagingSettings.Kafka,
            "azureServiceBus" => definition.Messaging?.Broker == MessagingSettings.AzureServiceBus,
            "brokerConnection" => definition.Messaging?.Broker is MessagingSettings.Kafka or MessagingSettings.AzureServiceBus,
            "publishesEvents" => IntegrationEvents.Of(definition).Count > 0,
            "hasEnums" => definition.Enums is { Count: > 0 },
            "hasTypes" => definition.Types is { Count: > 0 },
            "hasModelTypes" => definition.Enums is { Count: > 0 } || definition.Types is { Count: > 0 },
            _ when !FeatureSet.Names.Contains(feature, StringComparer.OrdinalIgnoreCase) => throw new StratumException(
                $"condición desconocida '{feature}'. Válidas: {string.Join(", ", FeatureSet.Names)}, " +
                "persistence, postgres, sqlserver, cleanDdd, verticalSlice, messaging, kafka, azureServiceBus, brokerConnection, publishesEvents, hasEnums, hasTypes, hasModelTypes " +
                "(y hasRequiredString, publishEvents, hasReferences, hasRequiredReferences, referenced, relations, hasOperations, hasLists, integrationEvents, publishCreated/Updated/Deleted en plantillas de entidad)."),
            _ => definition.Features.IsEnabled(feature),
        };
    }

    private static void RenderTemplateSet(
        string templateName,
        Dictionary<string, string> tokens,
        Func<string, bool> isEnabled,
        List<GeneratedFile> result,
        TeamTemplates? team)
    {
        foreach (var template in TemplateSource.Load(templateName, team))
        {
            if (!IsIncluded(template.Path, isEnabled))
            {
                continue;
            }

            var path = RenderPath(template.Path, tokens);
            var origin = template.IsCustom ? $".stratum/templates/{templateName}/{template.Path}" : $"{templateName}/{template.Path}";
            var content = TemplateEngine.Render(template.Content, tokens, isEnabled, origin);

            // A template wrapped entirely in "@stratum:if feature" renders empty when the feature is off: skip the file.
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            if (path.EndsWith(".g.cs", StringComparison.Ordinal))
            {
                content = GeneratedFileHash.Stamp(content);
            }

            if (result.Any(f => f.Path == path))
            {
                throw new StratumException($"Dos plantillas generan el mismo archivo {path} (la última: {origin}).");
            }

            result.Add(new GeneratedFile(path, content));
        }
    }

    public static string Serialize(ServiceDefinition definition) =>
        JsonSerializer.Serialize(definition, StratumJsonContext.Default.ServiceDefinition) + "\n";

    public static ServiceDefinition Deserialize(string json)
    {
        try
        {
            // msg.json (before 0.22) called the version "msgVersion".
            json = json.Replace("\"msgVersion\"", "\"stratumVersion\"", StringComparison.Ordinal);
            return JsonSerializer.Deserialize(json, StratumJsonContext.Default.ServiceDefinition)
                ?? throw new StratumException("stratum.json está vacío.");
        }
        catch (JsonException ex)
        {
            throw new StratumException($"stratum.json no es válido: {ex.Message}", ex);
        }
    }

    private static bool IsIncluded(string templatePath, Func<string, bool> isEnabled)
    {
        foreach (var (prefix, feature) in ConditionalPaths)
        {
            if (templatePath.StartsWith(prefix, StringComparison.Ordinal) && !isEnabled(feature))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Path segments starting with a single underscore become dotfiles (<c>_gitignore</c> → <c>.gitignore</c>,
    /// <c>_github</c> → <c>.github</c>); this avoids hidden files inside the template folder.
    /// Tokens use <c>__Service__</c> in paths.
    /// </summary>
    private static string RenderPath(string templatePath, Dictionary<string, string> tokens)
    {
        var segments = templatePath.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            foreach (var (key, value) in tokens)
            {
                segment = segment.Replace("__" + key + "__", value, StringComparison.Ordinal);
            }

            if (segment.Length > 1 && segment[0] == '_' && segment[1] != '_')
            {
                segment = "." + segment[1..];
            }

            segments[i] = segment;
        }

        return string.Join('/', segments);
    }
}
