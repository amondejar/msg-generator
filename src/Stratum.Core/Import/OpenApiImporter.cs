using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Stratum.Core.Generation;
using Stratum.Core.Model;

namespace Stratum.Core.Import;

/// <summary>What an OpenAPI document becomes in Stratum, and what it could not translate.</summary>
public sealed record OpenApiImport(
    string? Title,
    IReadOnlyList<EntityDefinition> Entities,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> ManualEndpoints);

/// <summary>The entities of an import, validated against the service they go into, and the report.</summary>
public sealed record PreparedImport(
    string ServiceName,
    string? Title,
    string Architecture,
    IReadOnlyList<EntityDefinition> Entities,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> ManualEndpoints)
{
    /// <summary>The report the CLI prints (and the Studio shows).</summary>
    public string Report()
    {
        var text = new StringBuilder();
        text.AppendLine($"OpenAPI{(Title is null ? string.Empty : $" \"{Title}\"")} → {ServiceName} ({Architecture}): {Entities.Count} entidad(es)");
        foreach (var entity in Entities)
        {
            text.AppendLine($"  {entity.Name} (/api/v1/{EntityCodeGen.Kebab(entity.Plural)}): {PropertyTypes.Spec(entity.Properties)}");
            foreach (var operation in entity.Operations ?? [])
            {
                text.AppendLine($"    operación {operation.Name}({PropertyTypes.Spec(operation.Parameters)}) → {operation.Event}");
            }
        }

        if (Notes.Count > 0)
        {
            text.AppendLine().AppendLine("No se ha podido traducir (revísalo):");
            foreach (var note in Notes)
            {
                text.AppendLine($"  ⚠ {note}");
            }
        }

        if (ManualEndpoints.Count > 0)
        {
            text.AppendLine().AppendLine($"Endpoints que no son CRUD: escríbelos tú en <Entidad>Endpoints.cs ({ManualEndpoints.Count}):");
            foreach (var endpoint in ManualEndpoints.Take(40))
            {
                text.AppendLine($"  {endpoint}");
            }

            if (ManualEndpoints.Count > 40)
            {
                text.AppendLine($"  … y {ManualEndpoints.Count - 40} más");
            }
        }

        return text.ToString().TrimEnd();
    }
}

/// <summary>
/// OpenAPI 3.x / Swagger 2.0 (JSON or YAML) → entities. Resources are found from their CRUD paths
/// (/orders, /orders/{id}); their schema gives the properties; a property that points to another resource
/// becomes a reference; POST /orders/{id}/cancel becomes an aggregate operation in clean-ddd. Everything else is
/// reported so the user knows what to write by hand.
/// </summary>
public static partial class OpenApiImporter
{
    private static readonly string[] Methods = ["get", "post", "put", "patch", "delete"];

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "CreatedAt", "UpdatedAt", "Created", "Updated", "CreatedOn", "UpdatedOn", "ModifiedAt",
    };

    [GeneratedRegex("^(New|Create|Update|Patch)(?=[A-Z])|(Full|Base|Summary|Detail|Details|Header)?(Request|Input|Dto|DTO|Response|Model|Resource|Body|Payload|Create|Update)$|(Full|Base|Summary|Detail|Details|Header)$")]
    private static partial Regex SchemaAffixes();

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonWord();

    public static JsonObject Load(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        JsonNode? node;
        try
        {
            node = trimmed.StartsWith('{') ? JsonNode.Parse(trimmed, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) : MiniYaml.Parse(trimmed);
        }
        catch (JsonException ex)
        {
            throw new StratumException($"La especificación no es JSON válido: {ex.Message}", ex);
        }

        if (node is not JsonObject doc || (doc["openapi"] is null && doc["swagger"] is null))
        {
            throw new StratumException("No parece una especificación OpenAPI (falta 'openapi' o 'swagger').");
        }

        return doc;
    }

    /// <summary>
    /// Import + the same validation as "stratum add entity", entity by entity, against <paramref name="current"/>
    /// (null for a new service). Entities that fail go to the notes; references to them become plain guids.
    /// </summary>
    public static PreparedImport Prepare(JsonObject doc, ServiceDefinition? current, string? name, string? architecture)
    {
        ArgumentNullException.ThrowIfNull(doc);
        architecture = current?.Architecture ?? architecture ?? Architectures.VerticalSlice;
        var import = Import(doc, architecture);
        var serviceName = current?.Service.Name ?? Model.ServiceName.Normalize(name ?? Pascal(import.Title ?? "Api"));
        var definition = current ?? new ServiceDefinition { Service = new ServiceInfo { Name = serviceName }, Architecture = architecture };
        var notes = import.Notes.ToList();
        var accepted = new List<EntityDefinition>();
        foreach (var entity in import.Entities)
        {
            try
            {
                accepted.Add(ServicePlanner.ValidateEntity(entity, definition with { Entities = [.. definition.Entities, .. accepted] }) with { Operations = entity.Operations });
            }
            catch (StratumException ex)
            {
                notes.Add($"{entity.Name}: {ex.Message}");
            }
        }

        var known = definition.Entities.Select(e => e.Name).Concat(accepted.Select(e => e.Name)).ToHashSet(StringComparer.Ordinal);
        accepted = [.. accepted.Select(e => e with
        {
            Properties = [.. e.Properties.Select(p => p.References is { } t && !known.Contains(t) ? p with { References = null } : p)],
        })];

        return new PreparedImport(serviceName, import.Title, architecture, accepted, notes, import.ManualEndpoints);
    }

    public static OpenApiImport Import(JsonObject doc, string architecture)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var notes = new List<string>();
        var manual = new List<string>();
        var schemas = (doc["components"]?["schemas"] ?? doc["definitions"]) as JsonObject ?? [];
        var paths = doc["paths"] as JsonObject ?? [];

        // 1. Resources: collection paths (/orders) and item paths (/orders/{id}) with the schema they carry.
        var resources = new Dictionary<string, Resource>(StringComparer.OrdinalIgnoreCase);
        var actions = new List<(string Collection, string Verb, string Method, string Path, JsonObject Operation)>();
        foreach (var (path, item) in paths)
        {
            if (item is not JsonObject pathItem)
            {
                continue;
            }

            var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var literal = segments.Where(s => !IsParameter(s)).ToList();
            foreach (var method in Methods)
            {
                if (pathItem[method] is not JsonObject operation)
                {
                    continue;
                }

                var shape = Shape(segments);
                if (shape is null)
                {
                    manual.Add($"{method.ToUpperInvariant()} {path}");
                    continue;
                }

                var (collection, kind, verb) = shape.Value;
                if (kind == PathKind.Action)
                {
                    actions.Add((collection, verb!, method, path, operation));
                    continue;
                }

                var resource = resources.TryGetValue(collection, out var r) ? r : resources[collection] = new Resource(collection);
                resource.Methods.Add($"{method.ToUpperInvariant()} {path}");
                foreach (var schema in SchemasOf(operation, method, kind, doc))
                {
                    resource.Schemas.Add(schema);
                }
            }
        }

        // 2. Entities: one per resource with a schema.
        var drafts = new List<(Resource Resource, string Name, string Schema)>();
        foreach (var resource in resources.Values)
        {
            var best = resource.Schemas
                .Where(s => schemas[s] is JsonObject)
                .OrderBy(s => SchemaAffixes().IsMatch(s) ? 1 : 0)        // "Order" before "OrderCreate"
                .ThenByDescending(s => Properties(schemas, s, doc).Count)
                .FirstOrDefault();
            if (best is null)
            {
                notes.Add($"/{resource.Collection}: no tiene un esquema con propiedades ({string.Join(", ", resource.Methods)}). Créala a mano.");
                continue;
            }

            var name = Pascal(SchemaAffixes().Replace(best, string.Empty));
            if (name.Length == 0 || drafts.Any(d => d.Name == name))
            {
                continue;
            }

            drafts.Add((resource, name, best));
        }

        var entityNames = drafts.ToDictionary(d => d.Schema, d => d.Name, StringComparer.Ordinal);
        foreach (var d in drafts)
        {
            foreach (var other in d.Resource.Schemas.Where(s => s != d.Schema))
            {
                entityNames.TryAdd(other, d.Name);
            }
        }

        var names = drafts.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var entities = new List<EntityDefinition>();
        foreach (var (resource, name, schema) in drafts)
        {
            var properties = new List<PropertyDefinition>();
            var required = Required(schemas, schema, doc);
            foreach (var (propertyName, propertySchema) in Properties(schemas, schema, doc))
            {
                var pascal = Pascal(propertyName);
                if (Skipped.Contains(pascal) || propertySchema?["readOnly"]?.GetValue<bool>() == true && pascal.EndsWith("At", StringComparison.Ordinal))
                {
                    continue;
                }

                var nullable = !required.Contains(propertyName) || propertySchema?["nullable"]?.GetValue<bool>() == true || IsNullableType(propertySchema);
                if (Map(propertySchema, schemas, doc, entityNames, names, pascal, out var type, out var target, out var problem))
                {
                    var finalName = target is not null && !pascal.EndsWith("Id", StringComparison.Ordinal) ? pascal + "Id" : pascal;
                    if (properties.Any(p => p.Name == finalName))
                    {
                        continue;
                    }

                    properties.Add(new PropertyDefinition { Name = finalName, Type = type!, Nullable = nullable || target == name, References = target });
                }
                else
                {
                    notes.Add($"{name}.{pascal}: {problem}");
                }
            }

            if (properties.Count == 0)
            {
                notes.Add($"{name}: su esquema '{schema}' no tiene propiedades que Stratum sepa generar. Créala a mano.");
                continue;
            }

            var plural = Pascal(resource.Collection);
            entities.Add(new EntityDefinition
            {
                Name = name,
                Plural = plural == name ? PropertyTypes.Pluralize(name) : plural,
                Properties = properties,
                Layout = EntityDefinition.CurrentLayout,
            });
        }

        // 3. Actions: POST /orders/{id}/cancel → operation Cancel (clean-ddd); otherwise written by hand.
        foreach (var action in actions)
        {
            var entity = entities.FirstOrDefault(e => string.Equals(Pascal(action.Collection), e.Plural, StringComparison.OrdinalIgnoreCase));
            if (architecture != Architectures.CleanDdd || entity is null || action.Method != "post")
            {
                manual.Add($"{action.Method.ToUpperInvariant()} {action.Path}");
                continue;
            }

            var verb = Pascal(action.Verb);
            var parameters = new List<PropertyDefinition>();
            var body = BodySchema(action.Operation, doc);
            if (body is not null)
            {
                var bodyRequired = RequiredOf(body, schemas, doc);
                foreach (var (pn, ps) in PropertiesOf(body, schemas, doc))
                {
                    if (Map(ps, schemas, doc, entityNames, names, Pascal(pn), out var type, out var target, out var problem))
                    {
                        parameters.Add(new PropertyDefinition { Name = Pascal(pn), Type = type!, Nullable = !bodyRequired.Contains(pn), References = target });
                    }
                    else
                    {
                        notes.Add($"{entity.Name}.{verb}({Pascal(pn)}): {problem}");
                    }
                }
            }

            try
            {
                var operation = ModelRules.ParseOperation(entity.Name, verb, null, null) with { Parameters = parameters };
                var index = entities.IndexOf(entity);
                if ((entity.Operations ?? []).Any(o => o.Name == operation.Name))
                {
                    continue;
                }

                entities[index] = entity with { Operations = [.. entity.Operations ?? [], operation] };
            }
            catch (StratumException ex)
            {
                notes.Add($"{action.Path}: {ex.Message}");
                manual.Add($"{action.Method.ToUpperInvariant()} {action.Path}");
            }
        }

        // 4. Required references in a cycle can't be created: make them optional.
        var probe = new ServiceDefinition { Service = new ServiceInfo { Name = "Probe" }, Architecture = architecture, Entities = entities };
        try
        {
            ModelRules.Validate(probe);
        }
        catch (StratumException ex) when (ex.Message.Contains("círculo", StringComparison.Ordinal))
        {
            notes.Add($"{ex.Message} → las referencias se importan como opcionales.");
            entities = [.. entities.Select(e => e with { Properties = [.. e.Properties.Select(p => p.References is null ? p : p with { Nullable = true })] })];
        }

        return new OpenApiImport(doc["info"]?["title"]?.GetValue<string>(), entities, notes, manual.Distinct().ToList());
    }

    private enum PathKind { Collection, Item, Action }

    private sealed class Resource(string collection)
    {
        public string Collection { get; } = collection;

        public List<string> Methods { get; } = [];

        public HashSet<string> Schemas { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>/api/v1/orders → collection; /orders/{id} → item; /orders/{id}/cancel → action. Null for anything else.</summary>
    private static (string Collection, PathKind Kind, string? Verb)? Shape(string[] segments)
    {
        // Skip prefixes like api/v1.
        var s = segments.SkipWhile(x => x.Equals("api", StringComparison.OrdinalIgnoreCase) || VersionSegment().IsMatch(x)).ToArray();
        return s.Length switch
        {
            1 when !IsParameter(s[0]) => (s[0], PathKind.Collection, null),
            2 when !IsParameter(s[0]) && IsParameter(s[1]) => (s[0], PathKind.Item, null),
            3 when !IsParameter(s[0]) && IsParameter(s[1]) && !IsParameter(s[2]) => (s[0], PathKind.Action, s[2]),
            _ => null,
        };
    }

    [GeneratedRegex("^v[0-9]+(\\.[0-9]+)*$")]
    private static partial Regex VersionSegment();

    private static bool IsParameter(string segment) => segment.StartsWith('{') && segment.EndsWith('}');

    /// <summary>Names of the component schemas an operation sends or returns.</summary>
    private static IEnumerable<string> SchemasOf(JsonObject operation, string method, PathKind kind, JsonObject doc)
    {
        if (method is "post" or "put" or "patch" && RefName(BodySchemaNode(operation)) is { } body)
        {
            yield return body;
        }

        foreach (var (code, response) in operation["responses"] as JsonObject ?? [])
        {
            if (!code.StartsWith('2'))
            {
                continue;
            }

            var schema = ResponseSchemaNode(response, doc);
            if (RefName(schema) is { } direct)
            {
                yield return direct;

                // Envelopes: { "animal": {$ref AnimalFull} } or { "animals": [ ... ], "page": {...} }.
                if (Resolve(schema!["$ref"]!.ToString(), doc)?["properties"] is JsonObject envelope && envelope.Count <= 3)
                {
                    foreach (var (_, p) in envelope)
                    {
                        if (RefName(p) is { } inner && Resolve(p!["$ref"]!.ToString(), doc)?["properties"] is JsonObject { Count: > 1 })
                        {
                            yield return inner;
                        }
                        else if (p?["type"]?.ToString() == "array" && RefName(p["items"]) is { } innerItem)
                        {
                            yield return innerItem;
                        }
                    }
                }
            }
            else if (schema?["type"]?.ToString() == "array" && RefName(schema["items"]) is { } item)
            {
                yield return item;
            }
            else if (kind == PathKind.Collection && schema?["properties"] is JsonObject page)
            {
                // Paged envelopes: { items: [Order], total: 3 }.
                foreach (var (_, p) in page)
                {
                    if (p?["type"]?.ToString() == "array" && RefName(p["items"]) is { } paged)
                    {
                        yield return paged;
                    }
                }
            }
        }
    }

    private static JsonNode? BodySchemaNode(JsonObject operation) =>
        operation["requestBody"]?["content"] is JsonObject content
            ? (content["application/json"] ?? content.FirstOrDefault().Value)?["schema"]
            : (operation["parameters"] as JsonArray)?.FirstOrDefault(p => p?["in"]?.ToString() == "body")?["schema"];

    private static JsonNode? ResponseSchemaNode(JsonNode? response, JsonObject doc)
    {
        if (response?["$ref"]?.ToString() is { } reference && Resolve(reference, doc) is { } resolved)
        {
            response = resolved;
        }

        return response?["content"] is JsonObject content
            ? (content["application/json"] ?? content.FirstOrDefault().Value)?["schema"]
            : response?["schema"];
    }

    private static JsonObject? BodySchema(JsonObject operation, JsonObject doc)
    {
        var node = BodySchemaNode(operation);
        return node?["$ref"]?.ToString() is { } reference ? Resolve(reference, doc) as JsonObject : node as JsonObject;
    }

    private static string? RefName(JsonNode? schema) =>
        schema?["$ref"]?.ToString() is { } reference && reference.StartsWith('#') ? reference[(reference.LastIndexOf('/') + 1)..] : null;

    private static JsonNode? Resolve(string reference, JsonObject doc)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        JsonNode? node = doc;
        foreach (var part in reference[2..].Split('/'))
        {
            node = node?[part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)];
        }

        return node;
    }

    private static List<(string Name, JsonNode? Schema)> Properties(JsonObject schemas, string name, JsonObject doc) =>
        schemas[name] is JsonObject schema ? PropertiesOf(schema, schemas, doc) : [];

    private static HashSet<string> Required(JsonObject schemas, string name, JsonObject doc) =>
        schemas[name] is JsonObject schema ? RequiredOf(schema, schemas, doc) : [];

    /// <summary>Properties of a schema, following $ref and merging allOf.</summary>
    private static List<(string Name, JsonNode? Schema)> PropertiesOf(JsonObject schema, JsonObject schemas, JsonObject doc, int depth = 0)
    {
        var result = new List<(string, JsonNode?)>();
        if (depth > 8)
        {
            return result;
        }

        if (schema["$ref"]?.ToString() is { } reference && Resolve(reference, doc) is JsonObject target)
        {
            return PropertiesOf(target, schemas, doc, depth + 1);
        }

        foreach (var part in schema["allOf"] as JsonArray ?? [])
        {
            if (part is JsonObject p)
            {
                result.AddRange(PropertiesOf(p, schemas, doc, depth + 1));
            }
        }

        foreach (var (name, value) in schema["properties"] as JsonObject ?? [])
        {
            result.RemoveAll(r => r.Item1 == name);
            result.Add((name, value));
        }

        return result;
    }

    private static HashSet<string> RequiredOf(JsonObject schema, JsonObject schemas, JsonObject doc, int depth = 0)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (depth > 8)
        {
            return result;
        }

        if (schema["$ref"]?.ToString() is { } reference && Resolve(reference, doc) is JsonObject target)
        {
            return RequiredOf(target, schemas, doc, depth + 1);
        }

        foreach (var part in schema["allOf"] as JsonArray ?? [])
        {
            if (part is JsonObject p)
            {
                result.UnionWith(RequiredOf(p, schemas, doc, depth + 1));
            }
        }

        foreach (var name in schema["required"] as JsonArray ?? [])
        {
            if (name?.ToString() is { } n)
            {
                result.Add(n);
            }
        }

        return result;
    }

    private static bool IsNullableType(JsonNode? schema) =>
        schema?["type"] is JsonArray types && types.Any(t => t?.ToString() == "null");

    private static string? TypeName(JsonNode? schema) => schema?["type"] switch
    {
        JsonArray types => types.Select(t => t?.ToString()).FirstOrDefault(t => t != "null"),
        { } single => single.ToString(),
        _ => null,
    };

    /// <summary>OpenAPI property → Stratum type, a reference to another entity, or the reason it can't be generated.</summary>
    private static bool Map(JsonNode? schema, JsonObject schemas, JsonObject doc, Dictionary<string, string> entityNames, HashSet<string> names,
        string propertyName, out string? type, out string? target, out string? problem)
    {
        type = null;
        target = null;
        problem = null;

        if (RefName(schema) is { } referenced)
        {
            if (entityNames.TryGetValue(referenced, out var entity))
            {
                type = "guid";
                target = entity;
                return true;
            }

            var resolved = Resolve(schema!["$ref"]!.ToString(), doc);
            if (resolved?["enum"] is not null || TypeName(resolved) is "string" or "integer" or "number" or "boolean")
            {
                return Map(resolved, schemas, doc, entityNames, names, propertyName, out type, out target, out problem);
            }

            problem = $"objeto anidado '{referenced}': Stratum no genera objetos dentro de una entidad (hazlo entidad propia con una referencia, o guárdalo en tu código)";
            return false;
        }

        if (schema?["allOf"] is JsonArray { Count: 1 } single)
        {
            return Map(single[0], schemas, doc, entityNames, names, propertyName, out type, out target, out problem);
        }

        var format = schema?["format"]?.ToString();
        switch (TypeName(schema))
        {
            case "string":
                type = format switch
                {
                    "uuid" => "guid",
                    "date" => "date",
                    "date-time" => "datetime",
                    _ => "string",
                };
                if (type == "guid" && propertyName.EndsWith("Id", StringComparison.Ordinal) && names.Contains(propertyName[..^2]))
                {
                    target = propertyName[..^2];
                }

                return true;
            case "integer":
                type = format == "int64" ? "long" : "int";
                return true;
            case "number":
                type = "decimal";
                return true;
            case "boolean":
                type = "bool";
                return true;
            case "array":
                problem = RefName(schema!["items"]) is { } items && entityNames.TryGetValue(items, out var child)
                    ? $"lista de {child}: dale a {child} una referencia a esta entidad ({child} → …Id) en lugar de la lista"
                    : "lista: Stratum no genera colecciones dentro de una entidad";
                return false;
            case "object":
                problem = "objeto anidado: Stratum no genera objetos dentro de una entidad";
                return false;
            default:
                problem = "tipo no indicado en la especificación";
                return false;
        }
    }

    /// <summary>"order_lines", "order-lines", "orderLines" → "OrderLines".</summary>
    public static string Pascal(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder();
        foreach (var word in NonWord().Split(text).Where(w => w.Length > 0))
        {
            sb.Append(char.ToUpper(word[0], CultureInfo.InvariantCulture)).Append(word.AsSpan(1));
        }

        var result = sb.ToString();
        return result.Length > 0 && char.IsDigit(result[0]) ? "N" + result : result;
    }
}
