using System.Globalization;
using System.Text.RegularExpressions;
using Stratum.Core.Model;

namespace Stratum.Core.Generation;

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
    /// <param name="definition">The service (item types of the lists, architecture); optional for callers that only need names.</param>
    public static Dictionary<string, string> Tokens(EntityDefinition entity, FeatureSet features, ServiceDefinition? definition = null)
    {
        var props = entity.Properties;
        var types = definition?.Types ?? [];
        var integration = definition?.Architecture == Architectures.CleanDdd ? "Integration." : string.Empty;
        var wire = props.Any(p => p.Type == PropertyTypes.ObjectType) && types.Count == 0 ? [] : props.Select(p => PropertyTypes.OnTheWire(p, types)).ToList();
        string EventParameters(string eventClass) => string.Join(", ", wire.Select(p => $"{PropertyTypes.ToCSharp(p, eventClass)} {p.Name}"));
        string EventArguments(string eventClass) => wire.Count == 0 ? string.Empty : string.Join(", ", props.Select(p => PropertyTypes.WireValue(p, "entity", integration + eventClass, types)));

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Entity"] = entity.Name,
            ["Entities"] = entity.Plural,
            ["entities-kebab"] = WordBoundary().Replace(entity.Plural, "-$1").ToLowerInvariant(),
            ["entities_snake"] = WordBoundary().Replace(entity.Plural, "_$1").ToLowerInvariant(),
            ["entity_snake"] = WordBoundary().Replace(entity.Name, "_$1").ToLowerInvariant(),
            ["entity-kebab"] = Kebab(entity.Name),
            ["IntegrationEventParameters"] = EventParameters(entity.Name + "Created"),
            ["IntegrationEventArguments"] = EventArguments(entity.Name + "Created"),
            ["CreatedEventParameters"] = EventParameters(entity.Name + "Created"),
            ["UpdatedEventParameters"] = EventParameters(entity.Name + "Updated"),
            ["CreatedEventArguments"] = EventArguments(entity.Name + "Created"),
            ["UpdatedEventArguments"] = EventArguments(entity.Name + "Updated"),
            ["IntegrationEventBody"] = ContractBody(wire),

            // --- shared / vertical slice ---
            ["EntityProperties"] = string.Join("\n\n", props.Select(p =>
                $"    public {PropertyTypes.EntityCSharp(p)} {p.Name} {{ get; set; }}{PropertyTypes.EntityInitializer(p)}")),
            ["PropertyConfigurations"] = string.Join("\n", props.Select(p => Configuration(p, entity, types)).Where(l => l.Length > 0)),
            ["RequestParameters"] = string.Join(",\n", props.Select(p => $"    {ValidationAttributes(p)}{PropertyTypes.ApiCSharp(p)} {p.Name}")),
            ["ResponseParameters"] = string.Join("\n", props.Select(p => $"    {PropertyTypes.ApiCSharp(p)} {p.Name},")),
            ["ResponseArguments"] = string.Join("\n", props.Select(p => $"            {PropertyTypes.ToApi(p, $"entity.{p.Name}")},")),
            ["SameDataAssertions"] = string.Join("\n", props.Select(p => $"        Assert.Equal(expected.{p.Name}, actual.{p.Name});")),
            ["CreateAssignments"] = string.Join("\n", props.Select(p => $"            {p.Name} = {PropertyTypes.FromRequest(p, $"request.{p.Name}")},")),
            ["UpdateAssignments"] = string.Join("\n", props.Select(p => $"        entity.{p.Name} = {PropertyTypes.FromRequest(p, $"request.{p.Name}")};")),
            ["RequestToEntity"] = string.Join("\n", props.Select(p => $"        {p.Name} = {PropertyTypes.FromRequest(p, p.Name)},")),
            ["RequestApplyTo"] = string.Join("\n", props.Select(p => $"        entity.{p.Name} = {PropertyTypes.FromRequest(p, p.Name)};")),
            ["SampleArguments"] = string.Join(",\n", props.Select(p => $"        {p.Name}: {PropertyTypes.Sample(p, types, contract: true)}")),
            ["TestClient"] = features.Auth ? "factory.CreateAuthenticatedClient()" : "factory.CreateClient()",

            // --- clean + DDD ---
            ["AggregateProperties"] = string.Join("\n\n", props.Select(p =>
                $"    public {PropertyTypes.EntityCSharp(p)} {p.Name} {{ get; private set; }}{PropertyTypes.EntityInitializer(p)}")),
            ["FactoryParameters"] = string.Join(", ", props.Select(p => $"{PropertyTypes.ToCSharp(p)} {Camel(p.Name)}")),
            ["FactoryArguments"] = string.Join(", ", props.Select(p => Camel(p.Name))),
            ["ApplyGuards"] = string.Join("\n\n", props.Where(p => IsString(p) || ValueRules.IsNumber(p) && p.Rules is not null).Select(Guard)
                .Concat(props.Where(p => p.IsObjectList).Select(p => ItemGuards(p, types)).Where(g => g.Length > 0))),
            ["ApplyAssignments"] = string.Join("\n", props.Select(p => p.IsList
                ? $"        {p.Name} = [.. {Camel(p.Name)}];"
                : $"        {p.Name} = {Camel(p.Name)};")),
            ["CommandParameters"] = string.Join(", ", props.Select(p => $"{PropertyTypes.ToCSharp(p)} {p.Name}")),
            ["CommandArguments"] = string.Join(", ", props.Select(p => $"command.{p.Name}")),
            ["RequestToCommand"] = string.Join(", ", props.Select(p => PropertyTypes.FromRequest(p, $"request.{p.Name}"))),
            ["EntityArguments"] = string.Join(", ", props.Select(p => $"entity.{p.Name}")),
            ["DomainSampleArguments"] = string.Join(", ", props.Select(p => PropertyTypes.Sample(p, types))),
            ["DomainInvalidArguments"] = InvalidArguments(props, types),
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
        var dependents = ModelRules.Dependents(definition, entity.Name, includeSelf: true, blockingOnly: true);
        var unique = references.Where(r => r.IsOneToOne).ToList();
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
            // 1–N: WithMany; 1–1: WithOne (a unique index on the foreign key). What deleting the target does, in the database.
            ["ReferenceConfigurations"] = string.Concat(references.Select(r => r.IsOneToOne
                ? $"        builder.HasOne<{EntityType(r.References!)}>().WithOne().HasForeignKey<{EntityType(entity.Name)}>(e => e.{r.Name}).OnDelete(DeleteBehavior.{EfDeleteBehavior(r)});\n"
                : $"        builder.HasOne<{EntityType(r.References!)}>().WithMany().HasForeignKey(e => e.{r.Name}).OnDelete(DeleteBehavior.{EfDeleteBehavior(r)});\n")),

            // 1–1: another row can't point to the same target (checked before the database's unique index says so).
            ["UniqueChecks"] = string.Join("\n\n", unique.Select(r => r.Nullable
                ? $"        if (request.{r.Name} is {{ }} {Camel(r.Name)}Value && await db.{entity.Plural}.AnyAsync(e => e.{r.Name} == {Camel(r.Name)}Value && e.Id != id, cancellationToken))\n        {{\n            errors[\"{r.Name}\"] = [$\"Ya hay un {entity.Name} para ese {r.References} (relación 1–1).\"];\n        }}"
                : $"        if (await db.{entity.Plural}.AnyAsync(e => e.{r.Name} == request.{r.Name} && e.Id != id, cancellationToken))\n        {{\n            errors[\"{r.Name}\"] = [$\"Ya hay un {entity.Name} para ese {r.References} (relación 1–1).\"];\n        }}")),
            ["UniqueParameters"] = string.Join(", ", unique.Select(r => $"{PropertyTypes.ToCSharp(r)} {Camel(r.Name)}")),
            ["UniqueArguments"] = string.Join(", ", unique.Select(r => $"command.{r.Name}")),
            ["UniqueQueries"] = string.Join("\n\n", unique.Select(r => r.Nullable
                ? $"        if ({Camel(r.Name)} is {{ }} {Camel(r.Name)}Value && await db.{entity.Plural}.AnyAsync(e => e.{r.Name} == {Camel(r.Name)}Value && e.Id != id, cancellationToken))\n        {{\n            return \"Ya hay un {entity.Name} para ese {r.References} (relación 1–1).\";\n        }}"
                : $"        if (await db.{entity.Plural}.AnyAsync(e => e.{r.Name} == {Camel(r.Name)} && e.Id != id, cancellationToken))\n        {{\n            return \"Ya hay un {entity.Name} para ese {r.References} (relación 1–1).\";\n        }}")),

            // GET /api/v1/<entities>?customerId=…: the rows of one parent (the "N" of a 1–N, or the one of a 1–1).
            ["ListFilterParameters"] = string.Concat(references.Select(r => $"Guid? {Camel(r.Name)}, ")),
            ["ListFilterParameterLines"] = string.Concat(references.Select(r => $"\n        Guid? {Camel(r.Name)},")),
            ["ListFilters"] = string.Concat(references.Select(r => $".Where(e => {Camel(r.Name)} == null || e.{r.Name} == {Camel(r.Name)})")),
            ["McpListFilterParameters"] = string.Concat(references.Select(r => $"[Description(\"Only the {entity.Plural} of this {r.References} (optional).\")] Guid? {Camel(r.Name)}, ")),
            ["McpListFilterParameterLines"] = string.Concat(references.Select(r => $"\n        [Description(\"Only the {entity.Plural} of this {r.References} (optional).\")] Guid? {Camel(r.Name)},")),
            ["ListFilterArguments"] = string.Concat(references.Select(r => $", {Camel(r.Name)}")),
            ["ListFilterRecordParameters"] = string.Concat(references.Select(r => $", Guid? {r.Name} = null")),
            ["ListFilterActive"] = string.Join(" || ", references.Select(r => $"query.{r.Name} is not null")),
            ["ListQueryFilters"] = string.Concat(references.Select(r => $".Where(e => query.{r.Name} == null || e.{r.Name} == query.{r.Name})")),
            ["ListIntegrityParameter"] = references.Count > 0 ? $", I{entity.Name}Integrity integrity" : string.Empty,

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
            ["TestSampleArguments"] = TestSample(entity.Properties, definition.Types),
            ["TestChangedArguments"] = TestSample(entity.Properties, definition.Types, changed: true),
            ["TestReferenceHelpers"] = string.Join("\n\n", required.Select(target =>
            {
                var t = definition.Entities.First(e => e.Name == target);
                var ns = $"global::{service}.Api.Features.{t.Plural}";
                return $"    private static async Task<Guid> Create{t.Name}Async(HttpClient client)\n    {{\n" +
                       $"        var response = await client.PostAsJsonAsync(new Uri(\"/api/v1/{Kebab(t.Plural)}\", UriKind.Relative), new {ns}.{t.Name}Request(\n" +
                       TestSample(t.Properties, definition.Types).Replace("        ", "            ", StringComparison.Ordinal) +
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
                $"        var done = await handler.HandleAsync(new {o.Name}{entity.Name}(id{string.Concat(o.Parameters.Select(p => $", {PropertyTypes.FromRequest(p, $"request.{p.Name}")}"))}), cancellationToken);\n" +
                "        return done ? TypedResults.NoContent() : TypedResults.NotFound();\n    }")),
            ["OperationRequests"] = string.Join("\n\n", operations.Where(o => o.Parameters.Count > 0).Select(o =>
                $"/// <summary>Body of POST /api/v1/{Kebab(entity.Plural)}/{{id}}/{Kebab(o.Name)}.</summary>\n" +
                $"public sealed record {o.Name}{entity.Name}Request(\n{string.Join(",\n", o.Parameters.Select(p => $"    {ValidationAttributes(p)}{PropertyTypes.ApiCSharp(p)} {p.Name}"))});")),
            ["OperationTools"] = string.Concat(operations.Select(o =>
                $"\n\n    [McpServerTool(Name = \"{WordBoundary().Replace(o.Name, "_$1").ToLowerInvariant()}_{WordBoundary().Replace(entity.Name, "_$1").ToLowerInvariant()}\", Title = \"{o.Name} a {entity.Name}\", OpenWorld = false)]\n" +
                $"    [Description(\"{entity.Name}.{o.Name}: business operation of the aggregate (raises {o.Event}).\")]\n" +
                $"    public static Task<string> {o.Name}Tool(\n        Guid id,\n" +
                (o.Parameters.Count > 0 ? $"        {o.Name}{entity.Name}Request request,\n" : string.Empty) +
                $"        ICommandHandler<{o.Name}{entity.Name}, bool> handler,\n        CancellationToken cancellationToken)\n    {{\n" +
                (o.Parameters.Count > 0 ? "        McpResults.Validate(request);\n" : string.Empty) +
                $"        return Rules(async () => McpResults.ToJson(await {o.Name}(id{(o.Parameters.Count > 0 ? ", request" : string.Empty)}, handler, cancellationToken)));\n    }}")),
            ["OperationEventMappings"] = string.Concat(operations.Select(o => OperationMapping(definition, entity, o)).Where(m => m.Length > 0)),
            ["OperationIntegrationEvents"] = string.Concat(IntegrationEvents.Of(definition, entity).Where(e => e.Operation is not null).Select(e =>
            {
                var payload = IntegrationEvents.Payload(definition, e);
                return $"\n\n/// <summary>{entity.Name}.{e.Operation!.Name} (published operation): the {entity.Name} after it and the operation's data.</summary>\n" +
                    $"[IntegrationEvent(\"{e.Topic}\")]\n" +
                    $"public sealed record {e.EventClass}(Guid Id{string.Concat(payload.Select(p => $", {PropertyTypes.ToCSharp(p, e.EventClass)} {p.Name}"))}) : IntegrationEvent{ContractBody(payload)}";
            })),
        };
    }

    /// <summary>
    /// Domain event of an operation → integration events: OrderUpdated (if the entity publishes its changes) and, for a
    /// published operation, its own event (OrderShipped) with the entity's data and the operation's parameters.
    /// </summary>
    private static string EfDeleteBehavior(PropertyDefinition reference) => reference.DeleteBehavior switch
    {
        DeleteBehaviors.Cascade => "Cascade",
        DeleteBehaviors.SetNull => "SetNull",
        _ => "Restrict",
    };

    private static string OperationMapping(ServiceDefinition definition, EntityDefinition entity, EntityOperation operation)
    {
        var events = new List<string>();
        if (entity.PublishesEvent(Subscription.Updated) && definition.Messaging is not null)
        {
            events.Add($"new Integration.{entity.Name}Updated(entity.Id, {string.Join(", ", entity.Properties.Select(p => PropertyTypes.WireValue(p, "entity", $"Integration.{entity.Name}Updated", definition.Types)))})");
        }

        if (IntegrationEvents.Of(definition, entity).FirstOrDefault(e => e.Operation == operation) is { } published)
        {
            var type = $"Integration.{published.EventClass}";
            var values = entity.Properties.Select(p => PropertyTypes.WireValue(p, "entity", type, definition.Types))
                .Concat(operation.Parameters.Where(p => !entity.Properties.Any(x => x.Name == p.Name)).Select(p => PropertyTypes.WireValue(p, "operation", type, definition.Types)));
            events.Add($"new {type}(entity.Id{string.Concat(values.Select(v => ", " + v))})");
        }

        if (events.Count == 0)
        {
            return string.Empty;
        }

        var pattern = events.Count > 1 || IntegrationEvents.Of(definition, entity).Any(e => e.Operation == operation) ? $"{operation.Event} operation" : operation.Event;
        return $"        ({pattern}, {entity.Name} entity) => [{string.Join(", ", events)}],\n";
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
            ["OperationBody"] = OperationBody(entity, operation),
        };
    }

    /// <summary>
    /// A head start for the rule of an operation on a list: AddItem(Item:OrderItem) adds it to the list of OrderItem;
    /// RemoveItem / other names get the lines to adapt as comments. Empty when the operation doesn't touch a list.
    /// </summary>
    private static string OperationBody(EntityDefinition entity, EntityOperation operation)
    {
        var lines = new List<string>();
        foreach (var p in operation.Parameters.Where(p => p.Type == PropertyTypes.ObjectType))
        {
            var lists = entity.Properties.Where(l => l.IsObjectList && l.Of == p.Of).ToList();
            if (lists.Count != 1)
            {
                continue;
            }

            var list = lists[0].Name;
            var arg = Camel(p.Name);
            if (operation.Name.StartsWith("Add", StringComparison.Ordinal) && !p.IsList)
            {
                lines.Add($"        {list}.Add({arg}{(p.Nullable ? "!" : string.Empty)});");
            }
            else if (operation.Name.StartsWith("Add", StringComparison.Ordinal))
            {
                lines.Add($"        {list}.AddRange({arg});");
            }
            else if (operation.Name.StartsWith("Replace", StringComparison.Ordinal) && p.IsList)
            {
                lines.Add($"        {list} = [.. {arg}];");
            }
        }

        foreach (var p in operation.Parameters.Where(p => p.Type != PropertyTypes.ObjectType && !p.IsList))
        {
            var list = entity.Properties.FirstOrDefault(l => l.IsObjectList);
            if (list is not null && operation.Name.StartsWith("Remove", StringComparison.Ordinal))
            {
                lines.Add($"        // For example: if ({list.Name}.RemoveAll(i => i.{p.Name} == {Camel(p.Name)}) == 0) throw new DomainException(\"No such item.\");");
                break;
            }
        }

        return lines.Count == 0 ? string.Empty : string.Join("\n", lines) + "\n";
    }

    private static string Parameters(IEnumerable<PropertyDefinition> parameters) =>
        string.Join(", ", parameters.Select(p => $"{PropertyTypes.ToCSharp(p)} {Camel(p.Name)}"));

    private static string Plural(ServiceDefinition definition, string entity) =>
        definition.Entities.FirstOrDefault(e => e.Name == entity)?.Plural ?? PropertyTypes.Pluralize(entity);

    /// <summary>Named arguments of a sample request for the API tests.</summary>
    private static string TestSample(IEnumerable<PropertyDefinition> properties, IReadOnlyList<ItemTypeDefinition>? types, bool changed = false) =>
        string.Join(",\n", properties.Select(p => $"        {p.Name}: {(p.References is null ? PropertyTypes.Sample(p, types, contract: true, changed: changed) : p.Nullable ? "null" : $"await Create{p.References}Async(client)")}"));

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
            ["EnumNamespace"] = clean ? $"{service}.Domain" : $"{service}.Api.Features",
            ["EnumDeclarations"] = string.Join("\n\n", (definition.Enums ?? []).OrderBy(e => e.Name, StringComparer.Ordinal).Select(e =>
                $"/// <summary>{e.Name} (stratum.json). Stored and sent as text: \"{e.Values[0]}\".</summary>\n" +
                $"[JsonConverter(typeof(JsonStringEnumConverter<{e.Name}>))]\n" +
                $"public enum {e.Name}\n{{\n{string.Join(",\n", e.Values.Select(v => "    " + v))},\n}}")),
            ["FeatureUsings"] = string.Join("\n", entities.Select(e => $"using {service}.Api.Features.{e.Plural};")),
            ["FeatureMaps"] = string.Join("\n", entities.Select(e => $"        api.Map{e.Name}Endpoints();")),
            ["DbSets"] = string.Join("\n\n", entities.Select(e => $"    public DbSet<{e.Name}> {e.Plural} => Set<{e.Name}>();").Concat(ReplicaCodeGen.DbSets(definition))),
            ["DomainUsings"] = string.Join("\n", entities.Select(e => $"using {service}.Domain.{e.Plural};")),
            ["InfrastructureUsings"] = string.Join("\n", entities.Select(e => $"using {service}.Infrastructure.Persistence.{e.Plural};")),
            ["RepositoryRegistrations"] = string.Join("\n", entities.Select(e => $"        services.AddScoped<I{e.Name}Repository, {e.Name}Repository>();")
                .Concat(entities.Where(e => e.References.Any() || ModelRules.Dependents(definition, e.Name, includeSelf: true, blockingOnly: true).Count > 0).Select(e =>
                    $"        services.AddScoped<global::{service}.Application.{e.Plural}.I{e.Name}Integrity, global::{service}.Infrastructure.Persistence.{e.Plural}.{e.Name}Integrity>();"))
                .Concat(ReplicaCodeGen.Registrations(definition))),
            ["TypeDeclarations"] = TypeDeclarations(definition),
            ["TypeContracts"] = TypeContracts(definition),
            ["HttpRequests"] = HttpRequests(definition),
            ["DbContextNamespace"] = clean ? $"{service}.Infrastructure.Persistence" : $"{service}.Api.Data",
            ["McpExpectedTools"] = string.Join(", ", entities.Where(e => e.IsEditable).Select(e => $"\"list_{WordBoundary().Replace(e.Plural, "_$1").ToLowerInvariant()}\"")),
        };
    }

    private static bool IsString(PropertyDefinition p) => p.Type.Equals("string", StringComparison.OrdinalIgnoreCase) && !p.IsList;

    /// <summary>
    /// Body of an integration event record: ";" or, when it carries object lists, the records of their items nested in
    /// it (OrderCreated.OrderItem), so the contract is self-contained and a subscriber can copy it as is.
    /// </summary>
    public static string ContractBody(IEnumerable<PropertyDefinition> wireProperties)
    {
        ArgumentNullException.ThrowIfNull(wireProperties);
        var items = wireProperties.Where(p => p.Type == PropertyTypes.ObjectType && p.Fields is not null).DistinctBy(p => p.Of).ToList();
        return items.Count == 0 ? ";"
            : "\n{\n" + string.Join("\n\n", items.Select(p =>
                $"    public sealed record {p.Of}({string.Join(", ", p.Fields!.Select(f => $"{PropertyTypes.ToCSharp(f)} {f.Name}"))});")) + "\n}";
    }

    /// <summary>
    /// Requests of the .http file: CRUD and operations of every entity, with sample bodies. Referenced entities first,
    /// so their ids come from the responses of the requests above (request variables).
    /// </summary>
    public static string HttpRequests(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var entities = definition.Entities.Where(e => e.IsEditable).ToList();
        var ordered = new List<EntityDefinition>();
        void Visit(EntityDefinition e, HashSet<string> path)
        {
            if (ordered.Contains(e) || !path.Add(e.Name))
            {
                return;
            }

            foreach (var target in e.References.Select(r => entities.FirstOrDefault(x => x.Name == r.References)).OfType<EntityDefinition>())
            {
                Visit(target, path);
            }

            ordered.Add(e);
        }

        foreach (var e in entities)
        {
            Visit(e, []);
        }

        var clean = definition.Architecture == Architectures.CleanDdd;
        var text = new System.Text.StringBuilder();
        foreach (var e in ordered)
        {
            var route = "{{baseUrl}}/api/v1/" + Kebab(e.Plural);
            var create = "create" + e.Name;
            var item = $"{route}/{{{{{create}.response.body.$.id}}}}";
            var body = HttpBody(e.Properties, definition, ordered);
            text.Append($"\n### List {e.Plural}\nGET {route}?page=1&pageSize=20\n{{{{auth}}}}");
            text.Append($"\n### Create {e.Name}\n# @name {create}\nPOST {route}\n{{{{auth}}}}Content-Type: application/json\n\n{body}\n");
            text.Append($"\n### Get the {e.Name} created above\nGET {item}\n{{{{auth}}}}");
            text.Append($"\n### Replace the data of that {e.Name} (lists included)\nPUT {item}\n{{{{auth}}}}Content-Type: application/json\n\n{body}\n");
            foreach (var o in clean ? e.Operations ?? [] : [])
            {
                text.Append($"\n### {o.Name} (operation of the aggregate)\nPOST {item}/{Kebab(o.Name)}\n{{{{auth}}}}");
                text.Append(o.Parameters.Count == 0 ? string.Empty : $"Content-Type: application/json\n\n{HttpBody(o.Parameters, definition, ordered)}\n");
            }

            text.Append($"\n### Unknown {e.Name} id: 404\nGET {route}/00000000-0000-0000-0000-000000000000\n{{{{auth}}}}");
        }

        // Deletes last, dependents first (the database refuses to delete what is still referenced: 409).
        foreach (var e in Enumerable.Reverse(ordered))
        {
            var item = "{{baseUrl}}/api/v1/" + Kebab(e.Plural) + $"/{{{{create{e.Name}.response.body.$.id}}}}";
            text.Append($"\n### Delete the {e.Name} created above\nDELETE {item}\n{{{{auth}}}}");
        }

        var auth = definition.Features.IsEnabled("auth") ? "Authorization: Bearer {{token}}\n" : string.Empty;
        return text.ToString().TrimStart('\n').Replace("{{auth}}", auth, StringComparison.Ordinal);
    }

    private static string HttpBody(IEnumerable<PropertyDefinition> properties, ServiceDefinition definition, IReadOnlyList<EntityDefinition> created) =>
        "{\n" + string.Join(",\n", properties.Select(p => $"  \"{CamelJson(p.Name)}\": {HttpValue(p, definition, created, "  ")}")) + "\n}";

    private static string HttpValue(PropertyDefinition p, ServiceDefinition definition, IReadOnlyList<EntityDefinition> created, string indent)
    {
        if (p.References is { } target)
        {
            return created.Any(e => e.Name == target) ? $"\"{{{{create{target}.response.body.$.id}}}}\"" : "null";
        }

        if (p.Type == PropertyTypes.ObjectType)
        {
            var item = (definition.Types ?? []).FirstOrDefault(t => t.Name == p.Of);
            var single = item is null ? "{}" : $"{{ {string.Join(", ", item.Properties.Select(x => $"\"{CamelJson(x.Name)}\": {HttpValue(x, definition, created, indent)}"))} }}";
            return p.IsList ? $"[\n{indent}  {single}\n{indent}]" : single;
        }

        var one = p.Type switch
        {
            "string" when p.Rules is not null && !p.IsList => System.Text.Json.JsonSerializer.Serialize(PropertyTypes.TextSample(p), HttpJson),
            "int" or "long" or "decimal" when p.Rules is { Min: not null } or { Max: not null } && !p.IsList => PropertyTypes.NumberSample(p),
            "string" => "\"sample\"",
            "int" or "long" => "1",
            "decimal" => "9.95",
            "bool" => "true",
            "guid" => "\"3fa85f64-5717-4562-b3fc-2c963f66afa6\"",
            "date" => "\"2026-01-01\"",
            "datetime" => "\"2026-01-01T12:00:00Z\"",
            PropertyTypes.EnumType => $"\"{(definition.Enums ?? []).FirstOrDefault(x => x.Name == p.Enum)?.Values[0] ?? string.Empty}\"",
            _ => "null",
        };
        return p.IsList ? $"[{one}]" : one;
    }

    private static readonly System.Text.Json.JsonSerializerOptions HttpJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string CamelJson(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>Declarations of the item types (stratum.json "types"): plain classes EF Core stores in their own table.</summary>
    public static string TypeDeclarations(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return string.Join("\n\n", (definition.Types ?? []).OrderBy(t => t.Name, StringComparer.Ordinal).Select(t =>
            $"/// <summary>Item of a list (stratum.json \"types\"). Stored in the table of the list, one row per item.</summary>\n" +
            $"public sealed class {t.Name}\n{{\n" +
            string.Join("\n\n", t.Properties.Select(p =>
                $"    public {PropertyTypes.ToCSharp(p)} {p.Name} {{ get; init; }}{PropertyTypes.EntityInitializer(p)}")) +
            "\n}"));
    }

    /// <summary>
    /// API contracts of the item types (OrderItemContract): what requests and responses carry, validated with the same
    /// rules as the entity's own fields, so the stored type is never exposed.
    /// </summary>
    public static string TypeContracts(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return string.Join("\n\n", (definition.Types ?? []).OrderBy(t => t.Name, StringComparer.Ordinal).Select(t =>
        {
            var name = PropertyTypes.ContractName(t.Name);
            return $"/// <summary>A {t.Name} in requests and responses.</summary>\n" +
                $"public sealed record {name}(\n{string.Join(",\n", t.Properties.Select(p => $"    {ValidationAttributes(p)}{PropertyTypes.ToCSharp(p)} {p.Name}"))})\n{{\n" +
                $"    public {t.Name} ToItem() => new()\n    {{\n{string.Join("\n", t.Properties.Select(p => $"        {p.Name} = {p.Name},"))}\n    }};\n\n" +
                $"    public static {name} From({t.Name} item)\n    {{\n        ArgumentNullException.ThrowIfNull(item);\n" +
                $"        return new({string.Join(", ", t.Properties.Select(p => $"item.{p.Name}"))});\n    }}\n}}";
        }));
    }

    /// <summary>Domain checks of the fields of the items of an object list (clean-ddd).</summary>
    private static string ItemGuards(PropertyDefinition list, IReadOnlyList<ItemTypeDefinition> types)
    {
        var item = types.FirstOrDefault(t => t.Name == list.Of);
        var fields = item?.Properties.Where(p => IsString(p) || ValueRules.IsNumber(p) && p.Rules is not null).ToList() ?? [];
        var arg = Camel(list.Name);
        var checks = new List<string>
        {
            $"            if (element is null)\n            {{\n                throw new DomainException(\"{list.Name} can't contain empty items.\");\n            }}",
        };
        checks.AddRange(fields.SelectMany(p => RuleChecks(p, $"element.{p.Name}", $"{list.Name}.{p.Name}", "            ")));
        return $"        foreach (var element in {arg})\n        {{\n{string.Join("\n\n", checks)}\n        }}";
    }

    /// <summary>"OrderLine" → "order-line" (topics, routes).</summary>
    public static string Kebab(string name) => WordBoundary().Replace(name, "-$1").ToLowerInvariant();

    internal static string Camel(string name)
    {
        var camel = char.ToLowerInvariant(name[0]) + name[1..];
        return CSharpKeywords.Contains(camel) ? "@" + camel : camel;
    }

    private static string Guard(PropertyDefinition p) => string.Join("\n\n", RuleChecks(p, Camel(p.Name), p.Name, "        "));

    /// <summary>
    /// The domain checks of a value (clean-ddd): required, length, format, pattern and range. The same rules as the
    /// attributes of the API request, so the domain stays valid whoever calls it.
    /// </summary>
    private static IEnumerable<string> RuleChecks(PropertyDefinition p, string value, string label, string indent)
    {
        string Check(string condition, string message) =>
            $"{indent}if ({condition})\n{indent}{{\n{indent}    throw new DomainException(\"{label} {message}\");\n{indent}}}";

        var r = p.Rules ?? new PropertyRules();
        if (IsString(p))
        {
            if (!p.Nullable)
            {
                yield return Check($"string.IsNullOrWhiteSpace({value})", "is required.");
            }

            var max = ValueRules.MaxLength(p);
            yield return Check(p.Nullable ? $"{value} is {{ Length: > {max} }}" : $"{value}.Length > {max}", $"must be at most {max} characters.");
            if (r.MinLength is { } min and > 0)
            {
                yield return Check(p.Nullable ? $"{value} is {{ Length: < {min} }}" : $"{value}.Length < {min}", $"must be at least {min} characters.");
            }

            if (r.Format is { } format)
            {
                var (attribute, what) = format switch
                {
                    TextFormats.Email => ("EmailAddressAttribute", "a valid email address"),
                    TextFormats.Url => ("UrlAttribute", "a valid http(s) URL"),
                    _ => ("PhoneAttribute", "a valid phone number"),
                };
                yield return Check($"!new System.ComponentModel.DataAnnotations.{attribute}().IsValid({value})", $"must be {what}.");
            }

            if (r.Pattern is { } pattern)
            {
                var regex = $"System.Text.RegularExpressions.Regex.IsMatch({value}, {Verbatim(pattern)})";
                yield return Check(p.Nullable ? $"{value} is not null && !{regex}" : $"!{regex}", "has an invalid format.");
            }
        }
        else if (ValueRules.IsNumber(p) && (r.Min is not null || r.Max is not null))
        {
            var bounds = new List<string>();
            if (r.Min is { } low)
            {
                bounds.Add($"< {Literal(low, p.Type)}");
            }

            if (r.Max is { } high)
            {
                bounds.Add($"> {Literal(high, p.Type)}");
            }

            var message = r.Min is { } a && r.Max is { } b ? $"must be between {ValueRules.Number(a)} and {ValueRules.Number(b)}."
                : r.Min is { } onlyLow ? $"must be at least {ValueRules.Number(onlyLow)}." : $"must be at most {ValueRules.Number(r.Max!.Value)}.";
            yield return Check($"{value} is {string.Join(" or ", bounds)}", message);
        }
    }

    /// <summary>A C# literal of the type of the property: 5, 5L, 5.5m.</summary>
    private static string Literal(decimal value, string type) => type switch
    {
        "long" => ValueRules.Number(value) + "L",
        "decimal" => ValueRules.Number(value) + "m",
        _ => ValueRules.Number(value),
    };

    /// <summary>A verbatim C# string: @"^\d{5}$".</summary>
    private static string Verbatim(string text) => "@\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string InvalidArguments(IReadOnlyList<PropertyDefinition> props, IReadOnlyList<ItemTypeDefinition> types)
    {
        var firstRequired = props.FirstOrDefault(p => IsString(p) && !p.Nullable);
        return string.Join(", ", props.Select(p => p == firstRequired ? "\" \"" : PropertyTypes.Sample(p, types)));
    }

    private static string ValidationAttributes(PropertyDefinition p)
    {
        if (p.Type == PropertyTypes.ObjectType && !p.IsList)
        {
            return p.Nullable ? string.Empty : "[property: Required] ";
        }

        var r = p.Rules ?? new PropertyRules();
        var attributes = new List<string>();
        if (IsString(p))
        {
            if (!p.Nullable)
            {
                attributes.Add("Required");
            }

            if (r.MinLength is { } min and > 0)
            {
                attributes.Add($"MinLength({min})");
            }

            attributes.Add($"MaxLength({ValueRules.MaxLength(p)})");
            switch (r.Format)
            {
                case TextFormats.Email: attributes.Add("EmailAddress"); break;
                case TextFormats.Url: attributes.Add("Url"); break;
                case TextFormats.Phone: attributes.Add("Phone"); break;
            }

            if (r.Pattern is { } pattern)
            {
                attributes.Add($"RegularExpression({Verbatim(pattern)})");
            }
        }
        else if (ValueRules.IsNumber(p) && (r.Min is not null || r.Max is not null))
        {
            var (type, lowest, highest) = p.Type switch
            {
                "int" => ("int", int.MinValue.ToString(CultureInfo.InvariantCulture), int.MaxValue.ToString(CultureInfo.InvariantCulture)),
                "long" => ("long", long.MinValue.ToString(CultureInfo.InvariantCulture), long.MaxValue.ToString(CultureInfo.InvariantCulture)),
                _ => ("decimal", decimal.MinValue.ToString(CultureInfo.InvariantCulture), decimal.MaxValue.ToString(CultureInfo.InvariantCulture)),
            };
            var low = r.Min is { } a ? ValueRules.Number(a) : lowest;
            var high = r.Max is { } b ? ValueRules.Number(b) : highest;
            attributes.Add($"Range(typeof({type}), \"{low}\", \"{high}\", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)");
        }

        return attributes.Count == 0 ? string.Empty : $"[property: {string.Join(", ", attributes)}] ";
    }

    private static string Configuration(PropertyDefinition p, EntityDefinition entity, IReadOnlyList<ItemTypeDefinition> types)
    {
        if (p.IsObjectList)
        {
            var item = types.FirstOrDefault(t => t.Name == p.Of);
            var lines = new List<string>
            {
                $"            items.ToTable(\"{Snake(entity.Name)}_{Snake(p.Name)}\");",
                $"            items.WithOwner().HasForeignKey(\"{entity.Name}Id\");",
                "            items.Property<int>(\"Id\");",
                "            items.HasKey(\"Id\");",
            };
            lines.AddRange((item?.Properties ?? []).Select(x => Configuration(x, "items", "i", "            ")).Where(l => l.Length > 0));
            return $"        builder.OwnsMany(e => e.{p.Name}, items =>\n        {{\n{string.Join("\n", lines)}\n        }});";
        }

        if (p.IsObject)
        {
            // One item inside the entity: its columns in the entity's table (ShippingAddress_Street…).
            var item = types.FirstOrDefault(t => t.Name == p.Of);
            var lines = (item?.Properties ?? []).Select(x => Configuration(x, "item", "i", "            ")).Where(l => l.Length > 0).ToList();
            var owns = lines.Count == 0
                ? $"        builder.OwnsOne(e => e.{p.Name});"
                : $"        builder.OwnsOne(e => e.{p.Name}, item =>\n        {{\n{string.Join("\n", lines)}\n        }});";
            return p.Nullable ? owns : owns + $"\n        builder.Navigation(e => e.{p.Name}).IsRequired();";
        }

        if (p.IsList)
        {
            return p.Type switch
            {
                PropertyTypes.EnumType => $"        builder.PrimitiveCollection(e => e.{p.Name}).ElementType(t => t.HasConversion<string>());",
                "decimal" => $"        builder.PrimitiveCollection(e => e.{p.Name}).ElementType(t => t.HasPrecision(18, 2));",
                _ => string.Empty,   // EF Core maps lists of basic types on its own (JSON column, or an array in PostgreSQL)
            };
        }

        return Configuration(p, "builder", "e", "        ");
    }

    private static string Configuration(PropertyDefinition p, string builder, string lambda, string indent) => p.Type.ToLowerInvariant() switch
    {
        "string" => $"{indent}{builder}.Property({lambda} => {lambda}.{p.Name}).HasMaxLength({ValueRules.MaxLength(p)})" +
                    (p.Nullable ? ";" : ".IsRequired();"),
        "decimal" => $"{indent}{builder}.Property({lambda} => {lambda}.{p.Name}).HasPrecision(18, 2);",
        PropertyTypes.EnumType => $"{indent}{builder}.Property({lambda} => {lambda}.{p.Name}).HasConversion<string>().HasMaxLength({PropertyTypes.EnumColumnLength})" +
                    (p.Nullable ? ";" : ".IsRequired();"),
        _ => string.Empty,
    };

    private static string Snake(string name) => WordBoundary().Replace(name, "_$1").ToLowerInvariant();
}
