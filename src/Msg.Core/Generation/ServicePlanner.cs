using Msg.Core.Model;

namespace Msg.Core.Generation;

/// <summary>
/// Computes what MSG would change in a service folder, without touching the disk. Used by the CLI
/// ("msg add entity") and by the Studio's review screen, so both always agree.
/// Ownership rules, so user code is never overwritten:
/// <list type="bullet">
/// <item><c>*.g.cs</c> and <c>msg.json</c> belong to MSG and are regenerated.</item>
/// <item>Entity, persistence and test-infrastructure files are created only if they don't exist.</item>
/// <item>Project files and appsettings only get the missing entries added.</item>
/// <item>Everything else is left untouched.</item>
/// </list>
/// </summary>
public static class ServicePlanner
{
    private static readonly HashSet<string> ReservedEntityNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ping", "Feature", "Features", "Data", "PagedResponse", "PagedResult", "Service", "Database",
    };

    /// <summary>A brand-new service: every file is created.</summary>
    public static ChangePlan NewService(string directory, ServiceDefinition definition, GenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new MsgException($"La carpeta '{Path.GetFullPath(directory)}' ya existe y no está vacía.");
        }

        var changes = ServiceGenerator.Generate(definition, options)
            .Select(f => new FileChange(f.Path, ChangeKind.Create, f.Content, null))
            .ToList();

        return new ChangePlan(directory, changes, []);
    }

    /// <summary>Normalizes and validates a new entity against the service it is added to.</summary>
    public static EntityDefinition ValidateEntity(EntityDefinition entity, ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(definition);

        var name = ServiceName.Normalize(entity.Name);
        if (ReservedEntityNames.Contains(name))
        {
            throw new MsgException($"'{name}' es un nombre reservado para entidades. Elige otro.");
        }

        var plural = string.IsNullOrWhiteSpace(entity.Plural) ? PropertyTypes.Pluralize(name) : ServiceName.Normalize(entity.Plural);
        if (plural == name)
        {
            throw new MsgException($"El plural de '{name}' no puede ser igual al singular.");
        }

        var clash = definition.Entities.FirstOrDefault(e =>
            e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || e.Plural.Equals(plural, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            throw new MsgException($"Ya existe la entidad '{clash.Name}' ({clash.Plural}).");
        }

        if (entity.Properties.Count == 0)
        {
            throw new MsgException($"La entidad '{name}' necesita al menos una propiedad.");
        }

        // Re-parse through the same rules as --props (names, types, reserved, duplicates).
        var spec = string.Join(", ", entity.Properties.Select(p => $"{p.Name}:{p.Type}{(p.Nullable ? "?" : string.Empty)}"));
        return new EntityDefinition { Name = name, Plural = plural, Properties = PropertyTypes.Parse(spec) };
    }

    public static string ResolveProvider(string? requested, PersistenceSettings? current)
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
            throw new MsgException($"Este servicio ya usa '{current.Provider}'. No se puede cambiar de base de datos.");
        }

        return current.Provider;
    }

    /// <summary>Adds entities (already validated) to an existing service.</summary>
    public static ChangePlan AddEntities(string directory, ServiceDefinition current, IReadOnlyList<EntityDefinition> entities, string provider, GenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(entities);

        var updated = current with
        {
            Persistence = new PersistenceSettings { Provider = provider },
            Entities = [.. current.Entities, .. entities],
        };

        var files = ServiceGenerator.Generate(updated, options);
        var service = current.Service.Name;
        var changes = new List<FileChange>();

        foreach (var file in files)
        {
            var target = Path.Combine(directory, file.Path);
            var previous = File.Exists(target) ? File.ReadAllText(target) : null;

            if (file.Path.EndsWith(".g.cs", StringComparison.Ordinal))
            {
                if (previous != file.Content)
                {
                    changes.Add(new FileChange(file.Path, previous is null ? ChangeKind.Create : ChangeKind.Regenerate, file.Content, previous));
                }
            }
            else if (previous is null && IsCreateOnce(file.Path, service))
            {
                changes.Add(new FileChange(file.Path, ChangeKind.Create, file.Content, null));
            }
        }

        string[] projectFiles =
        [
            "Directory.Packages.props",
            $"src/{service}.Api/{service}.Api.csproj",
            $"src/{service}.Infrastructure/{service}.Infrastructure.csproj",
            $"tests/{service}.Api.Tests/{service}.Api.Tests.csproj",
        ];

        foreach (var projectFile in projectFiles)
        {
            AddMerge(projectFile, ProjectFileMerger.MergeItems);
        }

        AddMerge($"src/{service}.Api/appsettings.Development.json", ProjectFileMerger.MergeConnectionStrings);

        // msgVersion stays as it was: adding an entity must not silently upgrade the Msg.* packages.
        var definitionPath = Path.Combine(directory, ServiceGenerator.DefinitionFileName);
        changes.Add(new FileChange(
            ServiceGenerator.DefinitionFileName,
            ChangeKind.Regenerate,
            ServiceGenerator.Serialize(updated with { MsgVersion = current.MsgVersion }),
            File.Exists(definitionPath) ? File.ReadAllText(definitionPath) : null));

        return new ChangePlan(directory, changes, CheckWiring(directory, service, current.Features));

        void AddMerge(string relative, Func<string, string, string?> merge)
        {
            var rendered = files.FirstOrDefault(f => f.Path == relative);
            var target = Path.Combine(directory, relative);
            if (rendered is null || !File.Exists(target))
            {
                return;
            }

            var previous = File.ReadAllText(target);
            if (merge(previous, rendered.Content) is { } merged)
            {
                changes.Add(new FileChange(relative, ChangeKind.Merge, merged, previous));
            }
        }
    }

    public static ServiceDefinition Load(string directory)
    {
        var path = Path.Combine(directory, ServiceGenerator.DefinitionFileName);
        if (!File.Exists(path))
        {
            throw new MsgException($"No hay {ServiceGenerator.DefinitionFileName} en '{Path.GetFullPath(directory)}'. ¿Es un servicio generado con MSG?");
        }

        return ServiceGenerator.Deserialize(File.ReadAllText(path));
    }

    private static bool IsCreateOnce(string path, string service) =>
        path.StartsWith($"src/{service}.Api/Features/", StringComparison.Ordinal)
        || path.StartsWith($"src/{service}.Api/Data/", StringComparison.Ordinal)
        || path.StartsWith($"src/{service}.Domain/", StringComparison.Ordinal)
        || path.StartsWith($"src/{service}.Application/", StringComparison.Ordinal)
        || path.StartsWith($"src/{service}.Infrastructure/", StringComparison.Ordinal)
        || path.StartsWith($"tests/{service}.Api.Tests/Features/", StringComparison.Ordinal)
        || path.StartsWith($"tests/{service}.Api.Tests/Domain/", StringComparison.Ordinal)
        || path == $"tests/{service}.Api.Tests/ApiFactory.cs"
        || path == $"tests/{service}.Api.Tests/TestAuthHandler.cs"
        || path == "compose.yaml";

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
}
