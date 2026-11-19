using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Msg.Core.Model;

namespace Msg.Core.Generation;

public sealed record AddEntityRequest
{
    public required string ServiceDirectory { get; init; }

    public required string Name { get; init; }

    public required string Properties { get; init; }

    public string? Plural { get; init; }

    /// <summary>Only used for the first entity (it decides the service's database).</summary>
    public string? Provider { get; init; }
}

public sealed record AddEntityResult(
    EntityDefinition Entity,
    string Provider,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Adds an entity to an existing service. Ownership rules, so user code is never overwritten:
/// <list type="bullet">
/// <item><c>*.g.cs</c> belong to MSG and are regenerated every time.</item>
/// <item>Entity, persistence and test-infrastructure files are created only if they don't exist.</item>
/// <item>Project files and appsettings only get the missing entries added (taken from what the templates would render).</item>
/// <item>Everything else is left untouched.</item>
/// </list>
/// </summary>
public static partial class EntityAdder
{
    private static readonly HashSet<string> ReservedEntityNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ping", "Feature", "Features", "Data", "PagedResponse", "Service", "Database",
    };

    [GeneratedRegex("""<(?<item>PackageVersion|PackageReference)\s+Include="(?<id>[^"]+)"[^>]*/>""")]
    private static partial Regex PackageItem();

    public static AddEntityResult Add(AddEntityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var root = request.ServiceDirectory;
        var definitionPath = Path.Combine(root, ServiceGenerator.DefinitionFileName);
        if (!File.Exists(definitionPath))
        {
            throw new MsgException($"No hay {ServiceGenerator.DefinitionFileName} en '{Path.GetFullPath(root)}'. Ejecuta el comando dentro de un servicio generado con MSG (o usa --path).");
        }

        var definition = ServiceGenerator.Deserialize(File.ReadAllText(definitionPath));
        var entity = BuildEntity(request, definition);
        var provider = ResolveProvider(request.Provider, definition.Persistence);

        var updatedDefinition = definition with
        {
            Persistence = new PersistenceSettings { Provider = provider },
            Entities = [.. definition.Entities, entity],
        };

        var files = ServiceGenerator.Generate(updatedDefinition);
        var service = definition.Service.Name;
        var created = new List<string>();
        var updated = new List<string>();

        foreach (var file in files)
        {
            var target = Path.Combine(root, file.Path);

            if (file.Path.EndsWith(".g.cs", StringComparison.Ordinal))
            {
                var existed = File.Exists(target);
                if (!existed || File.ReadAllText(target) != file.Content)
                {
                    Write(target, file.Content);
                    (existed ? updated : created).Add(file.Path);
                }
            }
            else if (IsCreateOnce(file.Path, service) && !File.Exists(target))
            {
                Write(target, file.Content);
                created.Add(file.Path);
            }
        }

        // Merge-only edits: add what's missing, keep everything the user changed.
        string[] projectFiles =
        [
            "Directory.Packages.props",
            $"src/{service}.Api/{service}.Api.csproj",
            $"tests/{service}.Api.Tests/{service}.Api.Tests.csproj",
        ];

        foreach (var projectFile in projectFiles)
        {
            var rendered = files.FirstOrDefault(f => f.Path == projectFile);
            var target = Path.Combine(root, projectFile);
            if (rendered is not null && File.Exists(target) && MergePackageItems(target, rendered.Content))
            {
                updated.Add(projectFile);
            }
        }

        var appSettings = $"src/{service}.Api/appsettings.Development.json";
        var renderedSettings = files.FirstOrDefault(f => f.Path == appSettings);
        if (renderedSettings is not null && MergeConnectionStrings(Path.Combine(root, appSettings), renderedSettings.Content))
        {
            updated.Add(appSettings);
        }

        // msgVersion stays as it was: adding an entity must not silently upgrade the Msg.* packages.
        File.WriteAllText(definitionPath, ServiceGenerator.Serialize(updatedDefinition with { MsgVersion = definition.MsgVersion }));
        updated.Add(ServiceGenerator.DefinitionFileName);

        return new AddEntityResult(entity, provider, created, updated, CheckWiring(root, service, definition.Features));
    }

    private static EntityDefinition BuildEntity(AddEntityRequest request, ServiceDefinition definition)
    {
        var name = ServiceName.Normalize(request.Name);
        if (ReservedEntityNames.Contains(name))
        {
            throw new MsgException($"'{name}' es un nombre reservado para entidades. Elige otro.");
        }

        var plural = request.Plural is null ? PropertyTypes.Pluralize(name) : ServiceName.Normalize(request.Plural);
        if (plural == name)
        {
            throw new MsgException($"El plural de '{name}' no puede ser igual al singular. Usa --plural.");
        }

        var clash = definition.Entities.FirstOrDefault(e =>
            e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || e.Plural.Equals(plural, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            throw new MsgException($"Ya existe la entidad '{clash.Name}' ({clash.Plural}).");
        }

        return new EntityDefinition { Name = name, Plural = plural, Properties = PropertyTypes.Parse(request.Properties) };
    }

    private static string ResolveProvider(string? requested, PersistenceSettings? current)
    {
        if (requested is not null && !PersistenceSettings.Providers.Contains(requested))
        {
            throw new MsgException($"Base de datos '{requested}' no soportada. Opciones: {string.Join(", ", PersistenceSettings.Providers)}.");
        }

        if (current is null)
        {
            return requested ?? PersistenceSettings.Postgres;
        }

        if (requested is not null && requested != current.Provider)
        {
            throw new MsgException($"Este servicio ya usa '{current.Provider}'. No se puede cambiar de base de datos con --db.");
        }

        return current.Provider;
    }

    private static bool IsCreateOnce(string path, string service) =>
        path.StartsWith($"src/{service}.Api/Features/", StringComparison.Ordinal)
        || path.StartsWith($"src/{service}.Api/Data/", StringComparison.Ordinal)
        || path.StartsWith($"tests/{service}.Api.Tests/Features/", StringComparison.Ordinal)
        || path == $"tests/{service}.Api.Tests/ApiFactory.cs"
        || path == $"tests/{service}.Api.Tests/TestAuthHandler.cs"
        || path == "compose.yaml";

    /// <summary>Adds PackageVersion/PackageReference lines present in <paramref name="rendered"/> but missing in the file.</summary>
    private static bool MergePackageItems(string path, string rendered)
    {
        var text = File.ReadAllText(path);
        var existing = PackageItem().Matches(text).Select(m => m.Groups["id"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = PackageItem().Matches(rendered).Where(m => !existing.Contains(m.Groups["id"].Value)).ToList();
        if (missing.Count == 0)
        {
            return false;
        }

        var item = missing[0].Groups["item"].Value;
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = string.Concat(missing.Select(m => $"    {m.Value}{nl}"));

        // Insert at the end of the last ItemGroup that already holds this kind of item; otherwise add a new ItemGroup.
        var lastItem = text.LastIndexOf($"<{item} ", StringComparison.Ordinal);
        var close = lastItem >= 0 ? text.IndexOf("</ItemGroup>", lastItem, StringComparison.Ordinal) : -1;
        if (close >= 0)
        {
            var lineStart = text.LastIndexOf('\n', close) + 1;
            text = text.Insert(lineStart, lines);
        }
        else
        {
            var end = text.LastIndexOf("</Project>", StringComparison.Ordinal);
            text = text.Insert(end, $"  <ItemGroup>{nl}{lines}  </ItemGroup>{nl}{nl}");
        }

        File.WriteAllText(path, text);
        return true;
    }

    private static bool MergeConnectionStrings(string path, string rendered)
    {
        if (!File.Exists(path) || JsonNode.Parse(rendered)?["ConnectionStrings"] is not JsonObject renderedStrings)
        {
            return false;
        }

        var settings = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject ?? throw new MsgException($"{path} no es un objeto JSON.");

        if (settings["ConnectionStrings"] is not JsonObject strings)
        {
            strings = [];
            settings["ConnectionStrings"] = strings;
        }

        var changed = false;
        foreach (var (key, value) in renderedStrings)
        {
            if (!strings.ContainsKey(key))
            {
                strings[key] = value?.DeepClone();
                changed = true;
            }
        }

        if (changed)
        {
            File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        return changed;
    }

    /// <summary>Services generated before 0.3.0 don't call the generated registration: tell the user what to add.</summary>
    private static List<string> CheckWiring(string root, string service, FeatureSet features)
    {
        var warnings = new List<string>();
        var program = Path.Combine(root, "src", $"{service}.Api", "Program.cs");
        var code = File.Exists(program) ? File.ReadAllText(program) : string.Empty;

        if (!code.Contains("AddFeatures()", StringComparison.Ordinal) || !code.Contains("MapFeatures()", StringComparison.Ordinal))
        {
            warnings.Add(
                $"src/{service}.Api/Program.cs no registra las entidades. Añade:\n" +
                $"      using {service}.Api.Features;\n" +
                "      builder.Services.AddValidation();\n" +
                "      builder.AddFeatures();       // después de AddValidatedOptions\n" +
                "      v1.MapFeatures();            // después de MapPingEndpoints");
        }
        else if (!code.Contains("AddValidation()", StringComparison.Ordinal))
        {
            warnings.Add($"src/{service}.Api/Program.cs: añade builder.Services.AddValidation(); para validar las peticiones.");
        }

        var defaultsTests = Path.Combine(root, "tests", $"{service}.Api.Tests", "ServiceDefaultsTests.cs");
        if (features.Tests && File.Exists(defaultsTests)
            && File.ReadAllText(defaultsTests).Contains("WebApplicationFactory<Program>", StringComparison.Ordinal))
        {
            warnings.Add(
                $"tests/{service}.Api.Tests/ServiceDefaultsTests.cs usa WebApplicationFactory<Program>: cámbialo por ApiFactory " +
                "para que los tests usen la base de datos SQLite en memoria.");
        }

        return warnings;
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
