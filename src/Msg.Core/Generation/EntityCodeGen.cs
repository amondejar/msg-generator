using System.Text.RegularExpressions;
using Msg.Core.Model;

namespace Msg.Core.Generation;

/// <summary>
/// Code fragments that depend on an entity's properties. The template engine has no loops on purpose:
/// lists are rendered here, in C#, and injected into the templates as tokens.
/// </summary>
public static partial class EntityCodeGen
{
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    [GeneratedRegex("(?<=[a-z0-9])([A-Z])")]
    private static partial Regex WordBoundary();

    public static bool HasRequiredString(EntityDefinition entity) =>
        entity.Properties.Any(p => IsString(p) && !p.Nullable);

    /// <summary>Tokens for one entity (merged with the service tokens).</summary>
    public static Dictionary<string, string> Tokens(EntityDefinition entity, FeatureSet features)
    {
        var props = entity.Properties;
        var max = PropertyTypes.DefaultStringLength;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Entity"] = entity.Name,
            ["Entities"] = entity.Plural,
            ["entities-kebab"] = WordBoundary().Replace(entity.Plural, "-$1").ToLowerInvariant(),
            ["entities_snake"] = WordBoundary().Replace(entity.Plural, "_$1").ToLowerInvariant(),
            ["entity_snake"] = WordBoundary().Replace(entity.Name, "_$1").ToLowerInvariant(),
            ["entity-kebab"] = Kebab(entity.Name),
            ["IntegrationEventParameters"] = string.Join(", ", props.Select(p => $"{PropertyTypes.ToCSharp(p)} {p.Name}")),

            // --- shared / vertical slice ---
            ["EntityProperties"] = string.Join("\n\n", props.Select(p =>
                $"    public {PropertyTypes.ToCSharp(p)} {p.Name} {{ get; set; }}" +
                (IsString(p) && !p.Nullable ? " = string.Empty;" : string.Empty))),
            ["PropertyConfigurations"] = string.Join("\n", props.Select(Configuration).Where(l => l.Length > 0)),
            ["RequestParameters"] = string.Join(",\n", props.Select(p => $"    {ValidationAttributes(p)}{PropertyTypes.ToCSharp(p)} {p.Name}")),
            ["ResponseParameters"] = string.Join("\n", props.Select(p => $"    {PropertyTypes.ToCSharp(p)} {p.Name},")),
            ["ResponseArguments"] = string.Join("\n", props.Select(p => $"            entity.{p.Name},")),
            ["CreateAssignments"] = string.Join("\n", props.Select(p => $"            {p.Name} = request.{p.Name},")),
            ["UpdateAssignments"] = string.Join("\n", props.Select(p => $"        entity.{p.Name} = request.{p.Name};")),
            ["RequestToEntity"] = string.Join("\n", props.Select(p => $"        {p.Name} = {p.Name},")),
            ["RequestApplyTo"] = string.Join("\n", props.Select(p => $"        entity.{p.Name} = {p.Name};")),
            ["SampleArguments"] = string.Join(",\n", props.Select(p => $"        {p.Name}: {PropertyTypes.Sample(p)}")),
            ["TestClient"] = features.Auth ? "factory.CreateAuthenticatedClient()" : "factory.CreateClient()",

            // --- clean + DDD ---
            ["AggregateProperties"] = string.Join("\n\n", props.Select(p =>
                $"    public {PropertyTypes.ToCSharp(p)} {p.Name} {{ get; private set; }}" +
                (IsString(p) && !p.Nullable ? " = string.Empty;" : string.Empty))),
            ["FactoryParameters"] = string.Join(", ", props.Select(p => $"{PropertyTypes.ToCSharp(p)} {Camel(p.Name)}")),
            ["FactoryArguments"] = string.Join(", ", props.Select(p => Camel(p.Name))),
            ["ApplyGuards"] = string.Join("\n\n", props.Where(IsString).Select(p => Guard(p, max))),
            ["ApplyAssignments"] = string.Join("\n", props.Select(p => $"        {p.Name} = {Camel(p.Name)};")),
            ["CommandParameters"] = string.Join(", ", props.Select(p => $"{PropertyTypes.ToCSharp(p)} {p.Name}")),
            ["CommandArguments"] = string.Join(", ", props.Select(p => $"command.{p.Name}")),
            ["RequestToCommand"] = string.Join(", ", props.Select(p => $"request.{p.Name}")),
            ["EntityArguments"] = string.Join(", ", props.Select(p => $"entity.{p.Name}")),
            ["DomainSampleArguments"] = string.Join(", ", props.Select(PropertyTypes.Sample)),
            ["DomainInvalidArguments"] = InvalidArguments(props),
        };
    }

    /// <summary>
    /// Tokens that depend on the other entities of the service: references (foreign keys, existence checks, test
    /// data) and aggregate operations. Empty strings when the entity has none.
    /// </summary>
    public static Dictionary<string, string> RelationTokens(ServiceDefinition definition, EntityDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(entity);
        var service = definition.Service.Name;
        var clean = definition.Architecture == Architectures.CleanDdd;
        var references = entity.References.ToList();
        var dependents = ModelRules.Dependents(definition, entity.Name, includeSelf: true);
        var operations = entity.Operations ?? [];

        string EntityType(string name) => clean
            ? $"global::{service}.Domain.{Plural(definition, name)}.{name}"
            : $"global::{service}.Api.Features.{Plural(definition, name)}.{name}";

        string Check(PropertyDefinition r, string value, string onMissing)
        {
            var target = Plural(definition, r.References!);
            var variable = Camel(r.Name) + "Value";
            return r.Nullable
                ? $"        if ({value} is {{ }} {variable} && !await db.{target}.AnyAsync(e => e.Id == {variable}, cancellationToken))\n        {{\n{onMissing}\n        }}"
                : $"        if (!await db.{target}.AnyAsync(e => e.Id == {value}, cancellationToken))\n        {{\n{onMissing}\n        }}";
        }

        var required = RequiredReferenceClosure(definition, entity);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Configuration: appended after the property configurations (ends with a newline when not empty).
            ["ReferenceConfigurations"] = string.Concat(references.Select(r =>
                $"        builder.HasOne<{EntityType(r.References!)}>().WithMany().HasForeignKey(e => e.{r.Name}).OnDelete(DeleteBehavior.Restrict);\n")),

            // Vertical slice: checks inside the endpoints.
            ["ReferenceChecks"] = string.Join("\n\n", references.Select(r => Check(r, $"request.{r.Name}",
                $"            errors[\"{r.Name}\"] = [$\"No existe ningún {r.References} con Id {{request.{r.Name}}}.\"];"))),
            ["DependentChecks"] = string.Join("\n\n", dependents.Select(d =>
                $"        if (await db.{d.Entity.Plural}.AnyAsync(e => e.{d.Property.Name} == id, cancellationToken))\n        {{\n            return \"{d.Entity.Plural} ({d.Entity.Name}.{d.Property.Name})\";\n        }}")),

            // Clean + DDD: I<Entity>Integrity (Application) and its implementation (Infrastructure).
            ["ReferenceParameters"] = string.Join(", ", references.Select(r => $"{PropertyTypes.ToCSharp(r)} {Camel(r.Name)}")),
            ["ReferenceArguments"] = string.Join(", ", references.Select(r => $"command.{r.Name}")),
            ["ReferenceQueries"] = string.Join("\n\n", references.Select(r => Check(r, Camel(r.Name),
                $"            return $\"No existe ningún {r.References} con Id {{{Camel(r.Name)}}}.\";"))),

            // Tests: referenced rows are created through the API first (required references only; optional → null).
            ["FirstRequiredReference"] = references.FirstOrDefault(r => !r.Nullable)?.Name ?? string.Empty,
            ["TestSampleArguments"] = TestSample(entity.Properties),
            ["TestReferenceHelpers"] = string.Join("\n\n", required.Select(target =>
            {
                var t = definition.Entities.First(e => e.Name == target);
                var ns = $"global::{service}.Api.Features.{t.Plural}";
                return $"    private static async Task<Guid> Create{t.Name}Async(HttpClient client)\n    {{\n" +
                       $"        var response = await client.PostAsJsonAsync(new Uri(\"/api/v1/{Kebab(t.Plural)}\", UriKind.Relative), new {ns}.{t.Name}Request(\n" +
                       TestSample(t.Properties).Replace("        ", "            ", StringComparison.Ordinal) +
                       $"));\n        response.EnsureSuccessStatusCode();\n" +
                       $"        var created = await response.Content.ReadFromJsonAsync<{ns}.{t.Name}Response>();\n" +
                       "        return created!.Id;\n    }";
            })),

            // Operations (clean + DDD).
            ["OperationEvents"] = string.Join("\n\n", operations.Select(o =>
                $"public sealed record {o.Event}(Guid {entity.Name}Id{string.Concat(o.Parameters.Select(p => $", {PropertyTypes.ToCSharp(p)} {p.Name}"))}) : DomainEvent;")),
            ["OperationDeclarations"] = string.Join("\n\n", operations.Select(o =>
                $"    /// <summary>Implemented by you in {entity.Name}.{o.Name}.cs; raises <see cref=\"{o.Event}\"/>.</summary>\n" +
                $"    public partial void {o.Name}({Parameters(o.Parameters)});")),
            ["OperationCommands"] = string.Join("\n\n", operations.Select(o =>
                $"/// <summary>{entity.Name}.{o.Name}. Returns false when the {entity.Name} does not exist.</summary>\n" +
                $"public sealed record {o.Name}{entity.Name}(Guid Id{string.Concat(o.Parameters.Select(p => $", {PropertyTypes.ToCSharp(p)} {p.Name}"))}) : ICommand<bool>;\n\n" +
                $"public sealed partial class {o.Name}{entity.Name}Handler(I{entity.Name}Repository repository, IUnitOfWork unitOfWork)\n" +
                $"    : ICommandHandler<{o.Name}{entity.Name}, bool>\n{{\n" +
                $"    public async Task<bool> HandleAsync({o.Name}{entity.Name} command, CancellationToken cancellationToken)\n    {{\n" +
                "        ArgumentNullException.ThrowIfNull(command);\n\n" +
                "        var entity = await repository.GetAsync(command.Id, cancellationToken);\n" +
                "        if (entity is null)\n        {\n            return false;\n        }\n\n" +
                $"        entity.{o.Name}({string.Join(", ", o.Parameters.Select(p => $"command.{p.Name}"))});\n" +
                "        await unitOfWork.SaveChangesAsync(cancellationToken);\n        return true;\n    }\n}")),
            ["OperationMaps"] = string.Join("\n", operations.Select(o =>
                $"        group.MapPost(\"/{{id:guid}}/{Kebab(o.Name)}\", {o.Name}).WithName(\"{o.Name}{entity.Name}\");")),
            ["OperationEndpoints"] = string.Join("\n\n", operations.Select(o =>
                $"    private static async Task<Results<NoContent, NotFound>> {o.Name}(\n        Guid id,\n" +
                (o.Parameters.Count > 0 ? $"        {o.Name}{entity.Name}Request request,\n" : string.Empty) +
                $"        ICommandHandler<{o.Name}{entity.Name}, bool> handler,\n        CancellationToken cancellationToken)\n    {{\n" +
                (o.Parameters.Count > 0 ? "        ArgumentNullException.ThrowIfNull(request);\n" : string.Empty) +
                $"        var done = await handler.HandleAsync(new {o.Name}{entity.Name}(id{string.Concat(o.Parameters.Select(p => $", request.{p.Name}"))}), cancellationToken);\n" +
                "        return done ? TypedResults.NoContent() : TypedResults.NotFound();\n    }")),
            ["OperationRequests"] = string.Join("\n\n", operations.Where(o => o.Parameters.Count > 0).Select(o =>
                $"/// <summary>Body of POST /api/v1/{Kebab(entity.Plural)}/{{id}}/{Kebab(o.Name)}.</summary>\n" +
                $"public sealed record {o.Name}{entity.Name}Request(\n{string.Join(",\n", o.Parameters.Select(p => $"    {ValidationAttributes(p)}{PropertyTypes.ToCSharp(p)} {p.Name}"))});")),
            ["OperationTools"] = string.Concat(operations.Select(o =>
                $"\n\n    [McpServerTool(Name = \"{WordBoundary().Replace(o.Name, "_$1").ToLowerInvariant()}_{WordBoundary().Replace(entity.Name, "_$1").ToLowerInvariant()}\", Title = \"{o.Name} a {entity.Name}\", OpenWorld = false)]\n" +
                $"    [Description(\"{entity.Name}.{o.Name}: business operation of the aggregate (raises {o.Event}).\")]\n" +
                $"    public static Task<string> {o.Name}Tool(\n        Guid id,\n" +
                (o.Parameters.Count > 0 ? $"        {o.Name}{entity.Name}Request request,\n" : string.Empty) +
                $"        ICommandHandler<{o.Name}{entity.Name}, bool> handler,\n        CancellationToken cancellationToken)\n    {{\n" +
                (o.Parameters.Count > 0 ? "        McpResults.Validate(request);\n" : string.Empty) +
                $"        return Rules(async () => McpResults.ToJson(await {o.Name}(id{(o.Parameters.Count > 0 ? ", request" : string.Empty)}, handler, cancellationToken)));\n    }}")),
            ["OperationEventMappings"] = string.Concat(operations.Select(o =>
                $"        ({o.Event}, {entity.Name} entity) => [new Integration.{entity.Name}Updated(entity.Id, {string.Join(", ", entity.Properties.Select(p => $"entity.{p.Name}"))})],\n")),
        };
    }

    /// <summary>Operation stub tokens (the create-once file {Entity}.{Operation}.cs).</summary>
    public static Dictionary<string, string> OperationTokens(EntityDefinition entity, EntityOperation operation)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(operation);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Operation"] = operation.Name,
            ["operation-kebab"] = Kebab(operation.Name),
            ["OperationEvent"] = operation.Event,
            ["OperationParameters"] = Parameters(operation.Parameters),
            ["OperationEventArguments"] = string.Concat(operation.Parameters.Select(p => ", " + Camel(p.Name))),
        };
    }

    private static string Parameters(IEnumerable<PropertyDefinition> parameters) =>
        string.Join(", ", parameters.Select(p => $"{PropertyTypes.ToCSharp(p)} {Camel(p.Name)}"));

    private static string Plural(ServiceDefinition definition, string entity) =>
        definition.Entities.FirstOrDefault(e => e.Name == entity)?.Plural ?? PropertyTypes.Pluralize(entity);

    /// <summary>Named arguments of a sample request for the API tests.</summary>
    private static string TestSample(IEnumerable<PropertyDefinition> properties) =>
        string.Join(",\n", properties.Select(p => $"        {p.Name}: {(p.References is null ? PropertyTypes.Sample(p) : p.Nullable ? "null" : $"await Create{p.References}Async(client)")}"));

    /// <summary>Entities a sample of <paramref name="entity"/> needs created first, dependencies before dependents.</summary>
    private static List<string> RequiredReferenceClosure(ServiceDefinition definition, EntityDefinition entity)
    {
        var order = new List<string>();
        void Add(EntityDefinition e)
        {
            foreach (var target in e.References.Where(r => !r.Nullable && r.References != e.Name).Select(r => r.References!))
            {
                if (order.Contains(target) || definition.Entities.FirstOrDefault(x => x.Name == target) is not { } t)
                {
                    continue;
                }

                Add(t);
                if (!order.Contains(target))
                {
                    order.Add(target);
                }
            }
        }

        Add(entity);
        order.Remove(entity.Name);
        return order;
    }

    /// <summary>Service-level tokens listing every entity (registration, DbSets, repositories).</summary>
    public static Dictionary<string, string> ServiceTokens(ServiceDefinition definition)
    {
        var service = definition.Service.Name;
        var entities = definition.Entities;
        var clean = definition.Architecture == Architectures.CleanDdd;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FeatureUsings"] = string.Join("\n", entities.Select(e => $"using {service}.Api.Features.{e.Plural};")),
            ["FeatureMaps"] = string.Join("\n", entities.Select(e => $"        api.Map{e.Name}Endpoints();")),
            ["DbSets"] = string.Join("\n\n", entities.Select(e => $"    public DbSet<{e.Name}> {e.Plural} => Set<{e.Name}>();")),
            ["DomainUsings"] = string.Join("\n", entities.Select(e => $"using {service}.Domain.{e.Plural};")),
            ["InfrastructureUsings"] = string.Join("\n", entities.Select(e => $"using {service}.Infrastructure.Persistence.{e.Plural};")),
            ["RepositoryRegistrations"] = string.Join("\n", entities.Select(e => $"        services.AddScoped<I{e.Name}Repository, {e.Name}Repository>();")
                .Concat(entities.Where(e => e.References.Any() || ModelRules.Dependents(definition, e.Name, includeSelf: true).Count > 0).Select(e =>
                    $"        services.AddScoped<global::{service}.Application.{e.Plural}.I{e.Name}Integrity, global::{service}.Infrastructure.Persistence.{e.Plural}.{e.Name}Integrity>();"))),
            ["DbContextNamespace"] = clean ? $"{service}.Infrastructure.Persistence" : $"{service}.Api.Data",
            ["McpExpectedTools"] = string.Join(", ", entities.Where(e => e.IsEditable).Select(e => $"\"list_{WordBoundary().Replace(e.Plural, "_$1").ToLowerInvariant()}\"")),
        };
    }

    private static bool IsString(PropertyDefinition p) => p.Type.Equals("string", StringComparison.OrdinalIgnoreCase);

    /// <summary>"OrderLine" → "order-line" (topics, routes).</summary>
    public static string Kebab(string name) => WordBoundary().Replace(name, "-$1").ToLowerInvariant();

    internal static string Camel(string name)
    {
        var camel = char.ToLowerInvariant(name[0]) + name[1..];
        return CSharpKeywords.Contains(camel) ? "@" + camel : camel;
    }

    private static string Guard(PropertyDefinition p, int max)
    {
        var arg = Camel(p.Name);
        var lines = new List<string>();
        if (!p.Nullable)
        {
            lines.Add($"        if (string.IsNullOrWhiteSpace({arg}))\n        {{\n            throw new DomainException(\"{p.Name} is required.\");\n        }}");
        }

        var lengthCheck = p.Nullable ? $"{arg} is {{ Length: > {max} }}" : $"{arg}.Length > {max}";
        lines.Add($"        if ({lengthCheck})\n        {{\n            throw new DomainException(\"{p.Name} must be at most {max} characters.\");\n        }}");
        return string.Join("\n\n", lines);
    }

    private static string InvalidArguments(IReadOnlyList<PropertyDefinition> props)
    {
        var firstRequired = props.FirstOrDefault(p => IsString(p) && !p.Nullable);
        return string.Join(", ", props.Select(p => p == firstRequired ? "\" \"" : PropertyTypes.Sample(p)));
    }

    private static string ValidationAttributes(PropertyDefinition p)
    {
        if (!IsString(p))
        {
            return string.Empty;
        }

        return p.Nullable
            ? $"[property: MaxLength({PropertyTypes.DefaultStringLength})] "
            : $"[property: Required, MaxLength({PropertyTypes.DefaultStringLength})] ";
    }

    private static string Configuration(PropertyDefinition p) => p.Type.ToLowerInvariant() switch
    {
        "string" => $"        builder.Property(e => e.{p.Name}).HasMaxLength({PropertyTypes.DefaultStringLength})" +
                    (p.Nullable ? ";" : ".IsRequired();"),
        "decimal" => $"        builder.Property(e => e.{p.Name}).HasPrecision(18, 2);",
        _ => string.Empty,
    };
}
