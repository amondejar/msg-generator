using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>A documentation file: <see cref="Generated"/> ones are Stratum's (rewritten); the rest are created once.</summary>
public sealed record SpecFile(string Path, string Content, bool Generated);

/// <summary>
/// The service told for people and for other models: a specification generated from stratum.json (what the service does,
/// always exact) and user stories with acceptance criteria in Gherkin (what for: created once, with the gaps only the
/// business can fill). Everything in Spanish, in the service's <c>docs/</c> folder.
/// </summary>
public static partial class SpecGenerator
{
    public const string Folder = "docs";
    public const string SpecPath = "docs/especificacion.g.md";
    public const string ContextPath = "docs/contexto.md";
    public const string StoriesFolder = "docs/historias";
    public const string SystemSpecPath = "docs/sistema.g.md";

    [GeneratedRegex(@"^(?<code>[A-Z]{2,4})-(?<number>\d{2,3})-(?<slug>[a-z0-9-]+)\.md$")]
    private static partial Regex StoryFile();

    /// <summary>True when the service already has its documentation (then every change keeps it up to date).</summary>
    public static bool HasDocs(string directory) => File.Exists(Path.Combine(directory, SpecPath));

    /// <summary>Every documentation file of the service: the specification, the context and one story per entity,
    /// operation and subscription (existing stories keep their number).</summary>
    public static IReadOnlyList<SpecFile> ForService(string directory, ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var files = new List<SpecFile> { new(SpecPath, Specification(definition), Generated: true) };
        files.Add(new SpecFile(ContextPath, Context(definition), Generated: false));

        var code = Code(definition.Service.Name);
        var existing = ExistingStories(directory, code);
        var next = existing.Count == 0 ? 1 : existing.Values.Max() + 1;
        foreach (var (slug, title, content) in Stories(definition))
        {
            if (!existing.TryGetValue(slug, out var number))
            {
                number = next++;
                existing[slug] = number;
            }

            var id = $"{code}-{number:00}";
            files.Add(new SpecFile($"{StoriesFolder}/{id}-{slug}.md", $"# {id} · {title}\n\n{content}", Generated: false));
        }

        return files;
    }

    /// <summary>What writing the documentation does: the specification is rewritten, the rest only created if missing.</summary>
    public static IReadOnlyList<FileChange> Changes(string directory, IEnumerable<SpecFile> files)
    {
        var changes = new List<FileChange>();
        foreach (var file in files)
        {
            var full = Path.Combine(directory, file.Path);
            var previous = File.Exists(full) ? File.ReadAllText(full) : null;
            if (previous is null)
            {
                changes.Add(new FileChange(file.Path, ChangeKind.Create, file.Content, null));
            }
            else if (file.Generated && Normalize(previous) != Normalize(file.Content))
            {
                changes.Add(new FileChange(file.Path, ChangeKind.Regenerate, file.Content, previous));
            }
        }

        return changes;
    }

    /// <summary>The documentation of the service as a plan (shown before writing, like any other change).</summary>
    public static ChangePlan Plan(string directory, ServiceDefinition definition) =>
        new(directory, Changes(directory, ForService(directory, definition)), []);

    /// <summary>Prefix of the stories of a service: "Orders" → "ORD".</summary>
    public static string Code(string service)
    {
        var letters = new string([.. service.Where(char.IsLetter)]).ToUpperInvariant();
        return letters.Length >= 3 ? letters[..3] : letters.PadRight(3, 'X');
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static Dictionary<string, int> ExistingStories(string directory, string code)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var folder = Path.Combine(directory, StoriesFolder);
        if (!Directory.Exists(folder))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.md"))
        {
            if (StoryFile().Match(Path.GetFileName(file)) is { Success: true } m && m.Groups["code"].Value == code)
            {
                result.TryAdd(m.Groups["slug"].Value, int.Parse(m.Groups["number"].Value, CultureInfo.InvariantCulture));
            }
        }

        return result;
    }

    // ---------------------------------------------------------------- specification

    private static string Specification(ServiceDefinition d)
    {
        var s = new StringBuilder();
        var name = d.Service.Name;
        var clean = d.Architecture == Architectures.CleanDdd;
        s.AppendLine($"# Especificación · {name}");
        s.AppendLine();
        s.AppendLine("> Generada por Stratum a partir de `stratum.json`: describe lo que el servicio hace hoy y se regenera con cada cambio,");
        s.AppendLine("> así que no la edites. Lo que solo sabe el negocio va en [contexto.md](contexto.md) y en las [historias](historias/).");
        s.AppendLine();

        s.AppendLine("## Resumen");
        s.AppendLine();
        s.AppendLine($"- **Arquitectura:** {(clean ? "Clean Architecture + DDD (Api → Application → Domain; Infrastructure)" : "vertical slices (cada entidad con su entidad, contratos y endpoints juntos)")}.");
        s.AppendLine($"- **Base de datos:** {d.Persistence?.Provider ?? "ninguna"}.");
        s.AppendLine($"- **Mensajería:** {(d.Messaging is { } m ? $"{m.Broker}, con outbox e inbox (cada evento se publica con su transacción y se procesa una sola vez)" : "no")}.");
        var features = new List<string>();
        if (d.Features.OpenApi)
        {
            features.Add("OpenAPI (`/openapi/v1.json`, y Scalar en `/scalar` en desarrollo)");
        }

        if (d.Features.Auth)
        {
            features.Add("autenticación JWT");
        }

        if (d.Features.Observability)
        {
            features.Add("OpenTelemetry");
        }

        if (d.Features.Resilience)
        {
            features.Add("resiliencia en las llamadas HTTP salientes");
        }

        if (d.Features.Mcp)
        {
            features.Add("servidor MCP");
        }

        features.Add("health checks (`/health`, `/alive`)");
        s.AppendLine($"- **Incluye:** {string.Join(", ", features)}.");
        s.AppendLine($"- **Entidades:** {(d.Entities.Count == 0 ? "ninguna todavía" : string.Join(", ", d.Entities.Select(e => $"[{e.Name}](#{Anchor(e.Name)})")))}.");
        var published = IntegrationEvents.Of(d);
        if (published.Count > 0)
        {
            s.AppendLine($"- **Publica:** {string.Join(", ", published.Select(p => $"`{p.EventClass}`"))}.");
        }

        if (d.Subscriptions is { Count: > 0 } subscriptions)
        {
            s.AppendLine($"- **Escucha:** {string.Join(", ", subscriptions.Select(x => $"`{x.EventClass}` de {x.Service}"))}.");
        }

        s.AppendLine();
        s.AppendLine("Convenciones de la API: JSON; errores en formato ProblemDetails (RFC 7807); `POST` responde 201 con `Location`,");
        s.AppendLine("`PUT` y `DELETE` 204, un id que no existe 404, datos no válidos 400 con un error por campo.");
        s.AppendLine("Los ids son UUID v7 (ordenados por creación); `CreatedAt` y `UpdatedAt` los pone el servicio.");
        s.AppendLine();

        foreach (var entity in d.Entities)
        {
            EntitySection(s, d, entity);
        }

        if (d.Enums is { Count: > 0 } enums)
        {
            s.AppendLine("## Enumerados");
            s.AppendLine();
            s.AppendLine("Se guardan y viajan como texto; un valor que no existe se rechaza con 400. El primero es el valor por defecto.");
            s.AppendLine();
            s.AppendLine("| Enumerado | Valores |");
            s.AppendLine("|---|---|");
            foreach (var en in enums)
            {
                s.AppendLine($"| `{en.Name}` | {string.Join(", ", en.Values.Select(v => $"`{v}`"))} |");
            }

            s.AppendLine();
        }

        if (d.Types is { Count: > 0 } types)
        {
            s.AppendLine("## Tipos de elemento");
            s.AppendLine();
            s.AppendLine("Los elementos de las listas de objetos: cada lista se guarda en una tabla propia y forma parte de su entidad.");
            s.AppendLine();
            foreach (var type in types)
            {
                s.AppendLine($"### {type.Name}");
                s.AppendLine();
                PropertyTable(s, d, type.Properties, withId: false);
            }
        }

        if (d.Subscriptions is { Count: > 0 } listens)
        {
            s.AppendLine("## Eventos que escucha");
            s.AppendLine();
            s.AppendLine("Llegan por el broker; la inbox garantiza que cada mensaje se procesa una sola vez aunque llegue repetido.");
            s.AppendLine();
            s.AppendLine("| Evento | De | Topic | Datos | Lo procesa |");
            s.AppendLine("|---|---|---|---|---|");
            foreach (var x in listens)
            {
                var data = string.Join(", ", new[] { "Id" }.Concat(x.Properties.Select(p => p.Name)));
                var handlers = new[] { x.HasHandler ? $"`{HandlerPath(d, x)}`" : null, ReplicaOf(d, x) is { } r ? $"la réplica `{r.Name}`" : null }.OfType<string>();
                s.AppendLine($"| `{x.EventClass}` | {x.Service} | `{x.Topic}` | {data} | {string.Join(" y ", handlers)} |");
            }

            s.AppendLine();
        }

        if (d.Replicas is { Count: > 0 } replicas)
        {
            s.AppendLine("## Réplicas de otros servicios");
            s.AppendLine();
            s.AppendLine("Copias locales de entidades de otros servicios, al día con sus eventos Created, Updated y Deleted (un evento antiguo que");
            s.AppendLine("llega tarde se ignora). Sirven para comprobar que algo existe o mostrar sus datos sin llamar al otro servicio. Son");
            s.AppendLine("consistentes a la larga: lo creado hace un momento puede no estar todavía, y solo tienen lo creado o cambiado desde que existen.");
            s.AppendLine();
            s.AppendLine("| Réplica | De | Tabla | Datos |");
            s.AppendLine("|---|---|---|---|");
            foreach (var r in replicas)
            {
                var table = "replica_" + EntityCodeGen.Kebab(PropertyTypes.Pluralize(r.Entity)).Replace('-', '_');
                s.AppendLine($"| `{r.Name}` | {r.Entity} de {r.Service} | `{table}` | {string.Join(", ", new[] { "Id" }.Concat(r.Properties.Select(p => p.Name)))} |");
            }

            s.AppendLine();
        }

        s.AppendLine("## Dónde va cada cosa");
        s.AppendLine();
        s.AppendLine("- `*.g.cs`, `stratum.json` y este archivo son de Stratum: se regeneran. El modelo (entidades, propiedades, operaciones,");
        s.AppendLine("  eventos) se cambia con `stratum` (CLI, Studio o servidor MCP), nunca editando el código generado.");
        s.AppendLine(clean
            ? "- Las reglas de negocio van en el agregado: `EnsureInvariants` en `<Entidad>.cs` y cada operación en `<Entidad>.<Operación>.cs` (lanza `DomainException` → 422)."
            : "- El código propio de cada entidad va en `<Entidad>.cs` y sus endpoints propios en `<Entidad>Endpoints.cs`.");
        s.AppendLine("- Lo que se hace al recibir un evento va en su handler (tabla de arriba).");
        s.AppendLine($"- Tests: `tests/{name}.Api.Tests` (xUnit, SQLite en memoria). `dotnet build` y `dotnet test` desde la carpeta del servicio.");
        return s.ToString();
    }

    private static void EntitySection(StringBuilder s, ServiceDefinition d, EntityDefinition entity)
    {
        var route = Route(entity);
        var clean = d.Architecture == Architectures.CleanDdd;
        s.AppendLine($"## {entity.Name}");
        s.AppendLine();
        s.AppendLine($"Recurso `{route}`.");
        s.AppendLine();
        PropertyTable(s, d, entity.Properties, withId: true);

        s.AppendLine("### API");
        s.AppendLine();
        s.AppendLine("| Método | Ruta | Respuestas |");
        s.AppendLine("|---|---|---|");
        s.AppendLine($"| GET | `{route}?page=1&pageSize=20` | 200 lista paginada (20 por página por defecto, 100 como máximo) |");
        foreach (var r in entity.References)
        {
            s.AppendLine($"| GET | `{route}?{Camel(r.Name)}={{id}}` | 200: {(r.IsOneToOne ? $"el {entity.Name} de un {r.References} (0 o 1)" : $"los {entity.Plural} de un {r.References}")}, paginados igual |");
        }

        s.AppendLine($"| GET | `{route}/{{id}}` | 200 · 404 |");
        s.AppendLine($"| POST | `{route}` | 201 con `Location` · 400 |");
        s.AppendLine($"| PUT | `{route}/{{id}}` | 204 · 400 · 404 |");
        var effects = string.Concat(DeleteEffects(d, entity).Select(x => x.Property.DeleteBehavior == DeleteBehaviors.Cascade
            ? $" · borra también sus {x.Entity.Plural} ({x.Entity.Name}.{x.Property.Name})"
            : $" · deja vacío {x.Entity.Name}.{x.Property.Name}"));
        s.AppendLine($"| DELETE | `{route}/{{id}}` | 204 · 404{(ReferencedBy(d, entity).Count > 0 ? " · 409 si lo usan otras entidades" : string.Empty)}{effects} |");
        foreach (var operation in Operations(d, entity))
        {
            s.AppendLine($"| POST | `{route}/{{id}}/{ServiceName.ToKebab(operation.Name)}` | 204 · {(operation.Parameters.Count > 0 ? "400 · " : string.Empty)}404 · 422 si una regla de negocio no lo permite |");
        }

        s.AppendLine();
        s.AppendLine("Ejemplo de cuerpo para `POST` y `PUT`:");
        s.AppendLine();
        s.AppendLine("```json");
        s.AppendLine(ExampleObject(d, entity.Properties, 0));
        s.AppendLine("```");
        s.AppendLine();

        if (Operations(d, entity) is { Count: > 0 } operations)
        {
            s.AppendLine("### Operaciones");
            s.AppendLine();
            s.AppendLine("| Operación | Parámetros | Evento de dominio | Se publica | Regla en |");
            s.AppendLine("|---|---|---|---|---|");
            foreach (var o in operations)
            {
                var parameters = o.Parameters.Count == 0 ? "—" : $"`{PropertyTypes.Spec(o.Parameters)}`";
                s.AppendLine($"| {o.Name} | {parameters} | `{o.Event}` | {(o.Publishes && d.Messaging is not null ? "sí" : "no")} | `{RulePath(d, entity, o)}` |");
            }

            s.AppendLine();
        }

        var events = clean
            ? new[] { $"{entity.Name}Created", $"{entity.Name}Updated", $"{entity.Name}Deleted" }.Concat(Operations(d, entity).Select(o => o.Event)).ToList()
            : [];
        if (events.Count > 0)
        {
            s.AppendLine($"Eventos de dominio (dentro del servicio, se procesan en `{entity.Name}EventHandlers.cs` después de guardar): {string.Join(", ", events.Select(e => $"`{e}`"))}.");
            s.AppendLine();
        }

        var published = IntegrationEvents.Of(d, entity);
        if (published.Count > 0)
        {
            s.AppendLine("### Eventos de integración que publica");
            s.AppendLine();
            s.AppendLine("| Evento | Topic | Cuándo | Datos |");
            s.AppendLine("|---|---|---|---|");
            foreach (var p in published)
            {
                var when = p.Operation is { } op ? $"al hacer {op.Name}" : p.Name switch
                {
                    Subscription.Created => "al crear",
                    Subscription.Updated => "al modificar (y en las operaciones)",
                    _ => "al eliminar",
                };
                var data = string.Join(", ", new[] { "Id" }.Concat(IntegrationEvents.Payload(d, p).Select(x => x.Name)));
                s.AppendLine($"| `{p.EventClass}` | `{p.Topic}` | {when} | {data} |");
            }

            s.AppendLine();
        }
    }

    private static void PropertyTable(StringBuilder s, ServiceDefinition d, IReadOnlyList<PropertyDefinition> properties, bool withId)
    {
        s.AppendLine("| Propiedad | Tipo | Obligatoria | Reglas |");
        s.AppendLine("|---|---|---|---|");
        if (withId)
        {
            s.AppendLine("| Id | guid | — | UUID v7, lo asigna el servicio |");
        }

        foreach (var p in properties)
        {
            s.AppendLine($"| {p.Name} | {TypeLabel(p)} | {(p.IsList ? "—" : p.Nullable ? "no" : "sí")} | {Rules(d, p)} |");
        }

        if (withId)
        {
            s.AppendLine("| CreatedAt, UpdatedAt | datetime | — | los pone el servicio |");
        }

        s.AppendLine();
    }

    private static string TypeLabel(PropertyDefinition p) =>
        p.References is { } target ? $"guid → {target}"
        : p.Type == PropertyTypes.ObjectType ? $"{p.Of}{(p.IsList ? "[]" : string.Empty)}"
        : p.Enum is { } en ? $"{en}{(p.IsList ? "[]" : string.Empty)}"
        : $"{p.Type}{(p.IsList ? "[]" : string.Empty)}";

    private static string Rules(ServiceDefinition d, PropertyDefinition p)
    {
        if (p.Type == PropertyTypes.ObjectType)
        {
            return p.IsList ? $"lista de {p.Of}: tabla propia; `PUT` la reemplaza entera" : $"un {p.Of}: sus columnas en la misma tabla{(p.Nullable ? string.Empty : " · obligatorio")}";
        }

        if (p.IsList)
        {
            return p.Enum is { } listEnum ? $"lista de valores de `{listEnum}`" : "lista (vacía por defecto)";
        }

        if (p.References is { } target)
        {
            var kind = p.IsOneToOne ? $"1–1: solo un {OwnerOf(d, p)} por {target}" : "1–N";
            var onDelete = p.DeleteBehavior switch
            {
                DeleteBehaviors.Cascade => $"al borrar el {target} se borra también",
                DeleteBehaviors.SetNull => $"al borrar el {target} se vacía",
                _ => $"el {target} no se puede borrar mientras lo use (409)",
            };
            return $"tiene que existir un {target} con ese id (si no, 400) · {kind} · {onDelete}";
        }


        if (p.Enum is { } en)
        {
            var values = d.Enums?.FirstOrDefault(x => x.Name == en)?.Values ?? [];
            return $"uno de: {string.Join(", ", values.Select(v => $"`{v}`"))}{(p.Nullable ? string.Empty : $"; si no se envía, `{values.FirstOrDefault()}`")}";
        }

        return p.Type switch
        {
            "string" => ValueRules.Describe(p.Rules, p) + (p.Nullable ? string.Empty : ", no vacío"),
            "int" or "long" when p.Rules is not null => ValueRules.Describe(p.Rules, p),
            "decimal" => "2 decimales (precisión 18, 2)" + (p.Rules is not null && ValueRules.Describe(p.Rules, p) is { Length: > 0 } range ? $", {range}" : string.Empty),
            "date" => "fecha (`2026-01-31`)",
            "datetime" => "fecha y hora con zona (`2026-01-31T10:00:00Z`)",
            _ => "—",
        };
    }

    private static string ExampleObject(ServiceDefinition d, IReadOnlyList<PropertyDefinition> properties, int level)
    {
        var pad = new string(' ', (level + 1) * 2);
        var lines = properties.Select(p => $"{pad}\"{Camel(p.Name)}\": {ExampleValue(d, p, level + 1)}");
        return "{\n" + string.Join(",\n", lines) + "\n" + new string(' ', level * 2) + "}";
    }

    private static string ExampleValue(ServiceDefinition d, PropertyDefinition p, int level)
    {
        if (p.Type == PropertyTypes.ObjectType)
        {
            var item = d.Types?.FirstOrDefault(t => t.Name == p.Of);
            if (!p.IsList)
            {
                return item is null ? "{}" : ExampleObject(d, item.Properties, level);
            }

            var pad = new string(' ', (level + 1) * 2);
            return $"[\n{pad}{(item is null ? "{}" : ExampleObject(d, item.Properties, level + 1))}\n{new string(' ', level * 2)}]";
        }

        string Single()
        {
            if (p.References is { } target)
            {
                return $"\"<id de un {target}>\"";
            }

            if (p.Enum is { } en)
            {
                return $"\"{d.Enums?.FirstOrDefault(x => x.Name == en)?.Values.FirstOrDefault() ?? en}\"";
            }

            return p.Type switch
            {
                "int" or "long" => "1",
                "decimal" => "9.95",
                "bool" => "true",
                "guid" => "\"0193f5b2-7c4e-7a10-9d3e-2b1c4d5e6f70\"",
                "date" => "\"2026-01-31\"",
                "datetime" => "\"2026-01-31T10:00:00Z\"",
                _ => $"\"{p.Name.ToLowerInvariant()} de ejemplo\"",
            };
        }

        return p.IsList ? $"[{Single()}]" : Single();
    }

    // ---------------------------------------------------------------- context and stories

    private static string Context(ServiceDefinition d)
    {
        var s = new StringBuilder();
        s.AppendLine($"# Contexto · {d.Service.Name}");
        s.AppendLine();
        s.AppendLine("> Tuyo: Stratum lo crea una vez y no lo vuelve a tocar. Es lo que no está en el modelo: para qué sirve el servicio,");
        s.AppendLine("> quién lo usa y qué significa cada cosa. Las historias de usuario y los modelos que trabajen con el servicio lo leen primero.");
        s.AppendLine();
        s.AppendLine("## Para qué sirve");
        s.AppendLine();
        s.AppendLine("_(por completar: el problema de negocio que resuelve, en dos o tres frases)_");
        s.AppendLine();
        s.AppendLine("## Roles");
        s.AppendLine();
        s.AppendLine("_(por completar: quién usa el servicio y para qué; las historias se refieren a estos roles)_");
        s.AppendLine();
        s.AppendLine("- **Rol 1**: …");
        s.AppendLine();
        s.AppendLine("## Glosario");
        s.AppendLine();
        s.AppendLine("| Término | Significado |");
        s.AppendLine("|---|---|");
        foreach (var entity in d.Entities)
        {
            s.AppendLine($"| {entity.Name} | _(por completar)_ |");
        }

        foreach (var en in d.Enums ?? [])
        {
            s.AppendLine($"| {en.Name} ({string.Join(", ", en.Values)}) | _(por completar: qué significa cada valor y cómo se pasa de uno a otro)_ |");
        }

        s.AppendLine();
        s.AppendLine("## Decisiones y restricciones");
        s.AppendLine();
        s.AppendLine("_(por completar: normativa, límites, lo que se decidió y por qué)_");
        return s.ToString();
    }

    private static IEnumerable<(string Slug, string Title, string Content)> Stories(ServiceDefinition d)
    {
        foreach (var entity in d.Entities)
        {
            yield return (ServiceName.ToKebab(entity.Name), $"Gestionar {entity.Name}", EntityStory(d, entity));
            foreach (var operation in Operations(d, entity))
            {
                yield return ($"{ServiceName.ToKebab(entity.Name)}-{ServiceName.ToKebab(operation.Name)}", $"{entity.Name}: {operation.Name}", OperationStory(d, entity, operation));
            }
        }

        foreach (var subscription in (d.Subscriptions ?? []).Where(x => x.HasHandler))
        {
            yield return ($"recibe-{ServiceName.ToKebab(subscription.Service)}-{ServiceName.ToKebab(subscription.EventClass)}",
                $"Al recibir {subscription.EventClass} de {subscription.Service}", SubscriptionStory(d, subscription));
        }
    }

    private static string Header(string story, string links) =>
        $"""
        > Historia de usuario: Stratum la crea una vez y no la vuelve a tocar. Completa lo que está _(por completar)_; los criterios
        > que ya trae son lo que el código generado garantiza (y lo que tienen que comprobar los tests).

        {story}

        **Estado:** borrador · {links}


        """;

    private static string EntityStory(ServiceDefinition d, EntityDefinition entity)
    {
        var route = Route(entity);
        var s = new StringBuilder();
        s.Append(Header(
            $"**Como** _(rol, ver [contexto](../contexto.md))_ **quiero** dar de alta, consultar, modificar y eliminar {entity.Name} **para** _(beneficio)_.",
            $"Especificación: [{entity.Name}](../especificacion.g.md#{Anchor(entity.Name)}) · API: `{route}`"));
        s.AppendLine("## Criterios de aceptación");
        s.AppendLine();
        s.AppendLine("```gherkin");
        s.AppendLine("# language: es");
        s.AppendLine($"Característica: Gestionar {entity.Name}");
        s.AppendLine();
        s.AppendLine($"  Escenario: Crear un {entity.Name} con datos válidos");
        s.AppendLine($"    Cuando envío POST {route} con los datos del ejemplo de la especificación");
        s.AppendLine($"    Entonces la respuesta es 201 con la ruta del {entity.Name} nuevo en Location");
        s.AppendLine($"    Y al consultarlo con GET {route}/{{id}} devuelve los mismos datos");
        if (entity.PublishesEvent(Subscription.Created) && d.Messaging is not null)
        {
            s.AppendLine($"    Y se publica {entity.Name}Created para otros servicios");
        }

        var required = entity.Properties.Where(p => p.Type == "string" && !p.Nullable && !p.IsList).ToList();
        if (required.Count > 0)
        {
            s.AppendLine();
            s.AppendLine("  Escenario: Faltan datos obligatorios");
            s.AppendLine($"    Cuando envío POST {route} sin {string.Join(", ", required.Select(p => p.Name))}");
            s.AppendLine("    Entonces la respuesta es 400 con un error por cada dato que falta");
        }

        var texts = entity.Properties.Where(p => p.Type == "string" && !p.IsList).ToList();
        if (texts.Count > 0)
        {
            s.AppendLine();
            s.AppendLine("  Escenario: Texto demasiado largo");
            s.AppendLine($"    Cuando envío POST {route} con {texts[0].Name} de {ValueRules.MaxLength(texts[0]) + 1} caracteres");
            s.AppendLine($"    Entonces la respuesta es 400 con el error de {texts[0].Name}");
        }

        foreach (var p in entity.Properties.Where(p => ValueRules.IsText(p) && p.Rules is { Format: not null } or { Pattern: not null }))
        {
            var what = p.Rules!.Format switch { TextFormats.Email => "que no es un email", TextFormats.Url => "que no es una URL", TextFormats.Phone => "que no es un teléfono", _ => "que no cumple su patrón" };
            s.AppendLine();
            s.AppendLine($"  Escenario: {p.Name} {what}");
            s.AppendLine($"    Cuando envío POST {route} con {p.Name} = \"no-valido\"");
            s.AppendLine($"    Entonces la respuesta es 400 con el error de {p.Name}");
        }

        foreach (var p in entity.Properties.Where(p => ValueRules.IsNumber(p) && p.Rules is { Min: not null } or { Max: not null }))
        {
            var outside = p.Rules!.Max is { } max ? ValueRules.Number(max + 1) : ValueRules.Number(p.Rules.Min!.Value - 1);
            s.AppendLine();
            s.AppendLine($"  Escenario: {p.Name} fuera de rango");
            s.AppendLine($"    Cuando envío POST {route} con {p.Name} = {outside}");
            s.AppendLine($"    Entonces la respuesta es 400 con el error de {p.Name}");
        }

        foreach (var p in entity.Properties.Where(p => p.Enum is not null && !p.IsList))
        {
            s.AppendLine();
            s.AppendLine($"  Escenario: {p.Name} con un valor que no existe");
            s.AppendLine($"    Cuando envío POST {route} con {p.Name} = \"NoExiste\"");
            s.AppendLine($"    Entonces la respuesta es 400 con el error de {p.Name}");
        }

        foreach (var p in entity.Properties.Where(p => p.References is not null))
        {
            s.AppendLine();
            s.AppendLine($"  Escenario: {p.Name} apunta a un {p.References} que no existe");
            s.AppendLine($"    Cuando envío POST {route} con un {p.Name} que no es de ningún {p.References}");
            s.AppendLine($"    Entonces la respuesta es 400 con el error de {p.Name}");
        }

        s.AppendLine();
        s.AppendLine($"  Escenario: Consultar un {entity.Name} que no existe");
        s.AppendLine($"    Cuando envío GET {route}/{{id}} con un id que no existe");
        s.AppendLine("    Entonces la respuesta es 404");
        s.AppendLine();
        s.AppendLine($"  Escenario: Listar {entity.Plural}");
        s.AppendLine($"    Dado que hay 25 {entity.Plural}");
        s.AppendLine($"    Cuando envío GET {route}?page=2&pageSize=20");
        s.AppendLine("    Entonces la respuesta es 200 con 5 elementos y el total 25");
        s.AppendLine();
        s.AppendLine($"  Escenario: Modificar un {entity.Name}");
        s.AppendLine($"    Dado un {entity.Name} existente");
        s.AppendLine($"    Cuando envío PUT {route}/{{id}} con datos válidos");
        s.AppendLine("    Entonces la respuesta es 204 y al consultarlo tiene los datos nuevos");
        foreach (var p in entity.Properties.Where(p => p.Type == PropertyTypes.ObjectType && p.IsList))
        {
            s.AppendLine($"    Y {p.Name} queda exactamente como se envió (se reemplaza entera)");
        }

        if (entity.PublishesEvent(Subscription.Updated) && d.Messaging is not null)
        {
            s.AppendLine($"    Y se publica {entity.Name}Updated para otros servicios");
        }

        s.AppendLine();
        s.AppendLine($"  Escenario: Eliminar un {entity.Name}");
        s.AppendLine($"    Dado un {entity.Name} existente");
        s.AppendLine($"    Cuando envío DELETE {route}/{{id}}");
        s.AppendLine("    Entonces la respuesta es 204 y al consultarlo la respuesta es 404");
        if (entity.PublishesEvent(Subscription.Deleted) && d.Messaging is not null)
        {
            s.AppendLine($"    Y se publica {entity.Name}Deleted para otros servicios");
        }

        foreach (var (other, property) in DeleteEffects(d, entity))
        {
            s.AppendLine();
            s.AppendLine($"  Escenario: Eliminar un {entity.Name} que tiene {other.Plural}");
            s.AppendLine($"    Dado un {other.Name} cuyo {property.Name} es ese {entity.Name}");
            s.AppendLine($"    Cuando envío DELETE {route}/{{id}}");
            s.AppendLine(property.DeleteBehavior == DeleteBehaviors.Cascade
                ? $"    Entonces la respuesta es 204 y ese {other.Name} también se borra"
                : $"    Entonces la respuesta es 204 y ese {other.Name} queda con {property.Name} vacío");
        }

        foreach (var p in entity.Properties.Where(p => p.IsOneToOne))
        {
            s.AppendLine();
            s.AppendLine($"  Escenario: Un segundo {entity.Name} para el mismo {p.References}");
            s.AppendLine($"    Dado un {entity.Name} cuyo {p.Name} es un {p.References}");
            s.AppendLine($"    Cuando envío POST {route} con ese mismo {p.Name}");
            s.AppendLine(d.Architecture == Architectures.CleanDdd
                ? "    Entonces la respuesta es 422: la relación es 1–1"
                : $"    Entonces la respuesta es 400 con el error de {p.Name}: la relación es 1–1");
        }

        foreach (var (other, property) in ReferencedBy(d, entity))
        {
            s.AppendLine();
            s.AppendLine($"  Escenario: Eliminar un {entity.Name} que usa un {other}");
            s.AppendLine($"    Dado un {other} cuyo {property} es ese {entity.Name}");
            s.AppendLine($"    Cuando envío DELETE {route}/{{id}}");
            s.AppendLine("    Entonces la respuesta es 409 y no se elimina");
        }

        s.AppendLine("```");
        s.AppendLine();
        RulesToComplete(s, d.Architecture == Architectures.CleanDdd
            ? $"`EnsureInvariants` en `{UserEntityPath(d, entity)}` (lanza `DomainException` → 422)"
            : $"`{UserEntityPath(d, entity)}` y `{entity.Name}Endpoints.cs`");
        return s.ToString();
    }

    private static string OperationStory(ServiceDefinition d, EntityDefinition entity, EntityOperation operation)
    {
        var route = $"{Route(entity)}/{{id}}/{ServiceName.ToKebab(operation.Name)}";
        var s = new StringBuilder();
        var with = operation.Parameters.Count == 0 ? string.Empty : $" indicando {string.Join(", ", operation.Parameters.Select(p => p.Name))}";
        s.Append(Header(
            $"**Como** _(rol, ver [contexto](../contexto.md))_ **quiero** hacer {operation.Name} sobre un {entity.Name}{with} **para** _(beneficio)_.",
            $"Especificación: [{entity.Name}](../especificacion.g.md#{Anchor(entity.Name)}) · API: `POST {route}` · Regla: `{RulePath(d, entity, operation)}`"));
        s.AppendLine("## Criterios de aceptación");
        s.AppendLine();
        s.AppendLine("```gherkin");
        s.AppendLine("# language: es");
        s.AppendLine($"Característica: {operation.Name} de {entity.Name}");
        s.AppendLine();
        s.AppendLine($"  Escenario: {operation.Name} cuando la regla lo permite");
        s.AppendLine($"    Dado un {entity.Name} existente _(en qué estado: por completar)_");
        s.AppendLine($"    Cuando envío POST {route}{(operation.Parameters.Count == 0 ? string.Empty : " con " + string.Join(" y ", operation.Parameters.Select(p => p.Name)))}");
        s.AppendLine("    Entonces la respuesta es 204");
        s.AppendLine($"    Y se registra el evento de dominio {operation.Event}");
        if (operation.Publishes && d.Messaging is not null)
        {
            s.AppendLine($"    Y se publica {operation.Event} para otros servicios");
        }

        s.AppendLine("    Y _(qué cambia en el " + entity.Name + ": por completar)_");
        s.AppendLine();
        s.AppendLine($"  Escenario: {entity.Name} que no existe");
        s.AppendLine($"    Cuando envío POST {route} con un id que no existe");
        s.AppendLine("    Entonces la respuesta es 404");
        var required = operation.Parameters.Where(p => !p.Nullable && !p.IsList && p.Type is "string" or PropertyTypes.ObjectType).ToList();
        if (required.Count > 0)
        {
            s.AppendLine();
            s.AppendLine("  Escenario: Faltan datos");
            s.AppendLine($"    Cuando envío POST {route} sin {string.Join(", ", required.Select(p => p.Name))}");
            s.AppendLine("    Entonces la respuesta es 400");
        }

        s.AppendLine();
        s.AppendLine("  # Una regla de negocio que no se cumple → 422 con su mensaje. Por ejemplo (por completar):");
        s.AppendLine($"  # Escenario: No se puede hacer {operation.Name} si …");
        s.AppendLine($"  #   Dado un {entity.Name} que …");
        s.AppendLine($"  #   Cuando envío POST {route}");
        s.AppendLine("  #   Entonces la respuesta es 422 con el mensaje \"…\"");
        s.AppendLine("```");
        s.AppendLine();
        RulesToComplete(s, $"`{RulePath(d, entity, operation)}` (lanza `DomainException` → 422)");
        return s.ToString();
    }

    private static string SubscriptionStory(ServiceDefinition d, Subscription x)
    {
        var s = new StringBuilder();
        var data = string.Join(", ", new[] { "Id" }.Concat(x.Properties.Select(p => p.Name)));
        s.Append(Header(
            $"**Como** {d.Service.Name} **quiero** reaccionar cuando {x.Service} publica `{x.EventClass}` **para** _(qué consigue el negocio)_.",
            $"Topic: `{x.Topic}` · Datos: {data} · Handler: `{HandlerPath(d, x)}`"));
        s.AppendLine("## Criterios de aceptación");
        s.AppendLine();
        s.AppendLine("```gherkin");
        s.AppendLine("# language: es");
        s.AppendLine($"Característica: {d.Service.Name} recibe {x.EventClass}");
        s.AppendLine();
        s.AppendLine($"  Escenario: Llega {x.EventClass}");
        s.AppendLine($"    Dado que {x.Service} publica {x.EventClass} con {data}");
        s.AppendLine($"    Cuando llega a {d.Service.Name}");
        s.AppendLine("    Entonces _(qué hace: por completar, p. ej. crea un … con …)_");
        s.AppendLine();
        s.AppendLine($"  Escenario: Llega dos veces el mismo {x.EventClass}");
        s.AppendLine("    Dado que el mensaje ya se procesó");
        s.AppendLine("    Cuando vuelve a llegar");
        s.AppendLine("    Entonces no se procesa otra vez (inbox)");
        s.AppendLine("```");
        s.AppendLine();
        RulesToComplete(s, $"`{HandlerPath(d, x)}`");
        return s.ToString();
    }

    private static void RulesToComplete(StringBuilder s, string where)
    {
        s.AppendLine("## Reglas de negocio");
        s.AppendLine();
        s.AppendLine("_(por completar: cada regla con su escenario en los criterios)_");
        s.AppendLine();
        s.AppendLine("- [ ] …");
        s.AppendLine();
        s.AppendLine("## Para implementarla");
        s.AppendLine();
        s.AppendLine($"- Las reglas van en {where}.");
        s.AppendLine("- Un test por escenario en `tests/` (los del código generado ya existen: añade los de las reglas).");
        s.AppendLine("- Si hace falta un dato, una operación o un evento nuevo, cámbialo en el modelo con `stratum` (CLI, Studio o MCP), no en el código generado.");
    }

    // ---------------------------------------------------------------- system

    /// <summary>The system as a whole: its services, who publishes what and who listens, with a diagram.</summary>
    public static SpecFile ForSystem(string systemName, IReadOnlyList<(string Directory, ServiceDefinition Definition)> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var s = new StringBuilder();
        s.AppendLine($"# Sistema · {systemName}");
        s.AppendLine();
        s.AppendLine("> Generada por Stratum a partir del `stratum.json` de cada servicio; se regenera con `stratum spec`. No la edites.");
        s.AppendLine();
        s.AppendLine("## Servicios");
        s.AppendLine();
        s.AppendLine("| Servicio | Arquitectura | Base de datos | Mensajería | Entidades | Especificación |");
        s.AppendLine("|---|---|---|---|---|---|");
        foreach (var (directory, d) in services.OrderBy(x => x.Definition.Service.Name, StringComparer.Ordinal))
        {
            var folder = directory.Replace('\\', '/').TrimEnd('/');
            s.AppendLine($"| {d.Service.Name} | {(d.Architecture == Architectures.CleanDdd ? "Clean + DDD" : "vertical")} | {d.Persistence?.Provider ?? "—"} | {d.Messaging?.Broker ?? "—"} | {(d.Entities.Count == 0 ? "—" : string.Join(", ", d.Entities.Select(e => e.Name)))} | [{folder}/{SpecPath}](../{folder}/{SpecPath}) |");
        }

        s.AppendLine();
        var routes = EventMap.Build(services.Select(x => x.Definition));
        s.AppendLine("## Eventos entre servicios");
        s.AppendLine();
        if (routes.Count == 0)
        {
            s.AppendLine("Ningún servicio publica ni escucha eventos todavía.");
            return new SpecFile(SystemSpecPath, s.ToString(), Generated: true);
        }

        s.AppendLine("```mermaid");
        s.AppendLine("flowchart LR");
        foreach (var (_, d) in services)
        {
            s.AppendLine($"  {d.Service.Name}[{d.Service.Name}]");
        }

        foreach (var route in routes.Where(r => r.Publisher is not null))
        {
            foreach (var consumer in route.Consumers)
            {
                s.AppendLine($"  {route.Publisher} -- {route.EventClass} --> {consumer}");
            }
        }

        s.AppendLine("```");
        s.AppendLine();
        s.AppendLine("| Evento | Topic | Publica | Lo escuchan | Estado |");
        s.AppendLine("|---|---|---|---|---|");
        foreach (var route in routes.OrderBy(r => r.Publisher).ThenBy(r => r.EventClass, StringComparer.Ordinal))
        {
            s.AppendLine($"| `{route.EventClass}` | `{route.Topic}` | {route.Publisher ?? "—"} | {(route.Consumers.Count == 0 ? "nadie" : string.Join(", ", route.Consumers))} | {route.Problem ?? "ok"} |");
        }

        return new SpecFile(SystemSpecPath, s.ToString(), Generated: true);
    }

    // ---------------------------------------------------------------- helpers

    private static IReadOnlyList<EntityOperation> Operations(ServiceDefinition d, EntityDefinition entity) =>
        d.Architecture == Architectures.CleanDdd ? entity.Operations ?? [] : [];

    private static List<(string Entity, string Property)> ReferencedBy(ServiceDefinition d, EntityDefinition entity) =>
        [.. d.Entities.SelectMany(e => e.Properties.Where(p => p.References == entity.Name && p.DeleteBehavior == DeleteBehaviors.Restrict).Select(p => (e.Name, p.Name)))];

    /// <summary>The references to <paramref name="entity"/> that act when it is deleted (cascade, setNull).</summary>
    private static List<(EntityDefinition Entity, PropertyDefinition Property)> DeleteEffects(ServiceDefinition d, EntityDefinition entity) =>
        [.. d.Entities.SelectMany(e => e.Properties.Where(p => p.References == entity.Name && p.DeleteBehavior != DeleteBehaviors.Restrict).Select(p => (e, p)))];

    private static string Route(EntityDefinition entity) => $"/api/v1/{ServiceName.ToKebab(entity.Plural)}";

    private static string RulePath(ServiceDefinition d, EntityDefinition entity, EntityOperation operation) =>
        $"src/{d.Service.Name}.Domain/{entity.Plural}/{entity.Name}.{operation.Name}.cs";

    private static string UserEntityPath(ServiceDefinition d, EntityDefinition entity) =>
        d.Architecture == Architectures.CleanDdd
            ? $"src/{d.Service.Name}.Domain/{entity.Plural}/{entity.Name}.cs"
            : $"src/{d.Service.Name}.Api/Features/{entity.Plural}/{entity.Name}.cs";

    private static ReplicaDefinition? ReplicaOf(ServiceDefinition d, Subscription x) =>
        (d.Replicas ?? []).FirstOrDefault(r => r.Service == x.Service && r.Entity == x.Entity && Subscription.Events.Contains(x.Event));

    private static string HandlerPath(ServiceDefinition d, Subscription x) =>
        d.Architecture == Architectures.CleanDdd
            ? $"src/{d.Service.Name}.Application/Events/{x.Service}/{x.EventClass}Handler.cs"
            : $"src/{d.Service.Name}.Api/Events/{x.Service}/{x.EventClass}Handler.cs";

    private static string Anchor(string heading) => heading.ToLowerInvariant();

    private static string OwnerOf(ServiceDefinition d, PropertyDefinition property) =>
        d.Entities.FirstOrDefault(e => e.Properties.Contains(property))?.Name ?? "registro";

    private static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
