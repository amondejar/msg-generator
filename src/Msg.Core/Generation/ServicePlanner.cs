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
        return new EntityDefinition { Name = name, Plural = plural, Properties = PropertyTypes.Parse(spec), Layout = EntityDefinition.CurrentLayout };
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

        return Plan(directory, current, updated, options);
    }

    /// <summary>
    /// New properties for an existing entity. <paramref name="renames"/> says which new names are old
    /// properties renamed (so the migration keeps the data); every other missing property is removed.
    /// </summary>
    public static ServiceDefinition UpdateEntity(
        ServiceDefinition current,
        string entityName,
        IReadOnlyList<PropertyDefinition> properties,
        IReadOnlyList<PropertyRename>? renames = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(properties);

        var entity = FindEntity(current, entityName);
        RequireEditable(entity);

        if (properties.Count == 0)
        {
            throw new MsgException($"La entidad '{entity.Name}' necesita al menos una propiedad.");
        }

        var spec = string.Join(", ", properties.Select(p => $"{p.Name}:{p.Type}{(p.Nullable ? "?" : string.Empty)}"));
        var parsed = PropertyTypes.Parse(spec);

        var pending = (current.PendingRenames ?? []).ToList();
        foreach (var rename in renames ?? [])
        {
            var from = entity.Properties.FirstOrDefault(p => p.Name.Equals(rename.From, StringComparison.OrdinalIgnoreCase))?.Name
                ?? throw new MsgException($"No se puede renombrar '{rename.From}': no es una propiedad de {entity.Name}.");
            var to = parsed.FirstOrDefault(p => p.Name.Equals(rename.To, StringComparison.OrdinalIgnoreCase))?.Name
                ?? throw new MsgException($"Renombrado {from} → {rename.To}: falta la propiedad '{rename.To}' en la nueva lista.");

            if (parsed.Any(p => p.Name == from))
            {
                throw new MsgException($"Renombrado {from} → {to}: '{from}' sigue en la lista de propiedades.");
            }

            if (entity.Properties.Any(p => p.Name == to))
            {
                throw new MsgException($"Renombrado {from} → {to}: '{to}' ya existe en {entity.Name}.");
            }

            // Chain with a rename not yet migrated: A → B, then B → C, is A → C.
            var previous = pending.FirstOrDefault(r => r.Entity == entity.Name && r.To == from);
            if (previous is not null)
            {
                pending.Remove(previous);
                from = previous.From;
            }

            if (from != to)
            {
                pending.Add(new PropertyRename { Entity = entity.Name, From = from, To = to });
            }
        }

        // A renamed property that is now removed: the column is dropped anyway.
        pending.RemoveAll(r => r.Entity == entity.Name && !parsed.Any(p => p.Name == r.To));

        return current with
        {
            Entities = [.. current.Entities.Select(e => e == entity ? entity with { Properties = parsed } : e)],
            PendingRenames = pending.Count == 0 ? null : pending,
        };
    }

    public static ServiceDefinition RemoveEntity(ServiceDefinition current, string entityName)
    {
        ArgumentNullException.ThrowIfNull(current);
        var entity = FindEntity(current, entityName);
        var pending = (current.PendingRenames ?? []).Where(r => r.Entity != entity.Name).ToList();

        // Persistence stays: the database (and its migrations) still exist.
        return current with
        {
            Entities = [.. current.Entities.Where(e => e != entity)],
            PendingRenames = pending.Count == 0 ? null : pending,
        };
    }

    /// <summary>An entity created before MSG 0.10 moves to the editable layout (*.g.cs + partial classes).</summary>
    public static ServiceDefinition ConvertEntity(ServiceDefinition current, string entityName)
    {
        ArgumentNullException.ThrowIfNull(current);
        var entity = FindEntity(current, entityName);
        if (entity.IsEditable)
        {
            throw new MsgException($"La entidad '{entity.Name}' ya tiene el formato editable.");
        }

        return current with
        {
            Entities = [.. current.Entities.Select(e => e == entity ? e with { Layout = EntityDefinition.CurrentLayout } : e)],
        };
    }

    public static EntityDefinition FindEntity(ServiceDefinition definition, string name)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Entities.FirstOrDefault(e => e.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new MsgException(
                $"No existe la entidad '{name}'. Entidades: {(definition.Entities.Count == 0 ? "(ninguna)" : string.Join(", ", definition.Entities.Select(e => e.Name)))}.");
    }

    private static void RequireEditable(EntityDefinition entity)
    {
        if (!entity.IsEditable)
        {
            throw new MsgException(
                $"La entidad '{entity.Name}' se creó con una versión de MSG anterior a la 0.10: su código es tuyo y MSG no puede " +
                $"cambiarla sin pisarlo. Conviértela primero al formato editable (msg entity convert {entity.Name}, o desde el Studio); " +
                "MSG te enseñará qué archivos cambia antes de escribir nada.");
        }
    }

    /// <summary>
    /// Everything MSG has to write, replace or delete to go from <paramref name="current"/> to
    /// <paramref name="updated"/> (the msg.json on disk and the one designed in the CLI or Studio).
    /// </summary>
    /// <param name="deleteOwnFiles">Also delete the user's files of removed entities (otherwise they are only reported).</param>
    public static ChangePlan Plan(string directory, ServiceDefinition current, ServiceDefinition updated, GenerationOptions? options = null, bool deleteOwnFiles = false)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(updated);

        var files = ServiceGenerator.Generate(updated, options);
        var newPaths = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var oldPaths = ServiceGenerator.Generate(current, options).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var service = current.Service.Name;
        var changes = new List<FileChange>();
        var warnings = new List<string>();
        var handled = new HashSet<string>(StringComparer.Ordinal);
        var ownLeft = new List<string>();

        // 1. Entities converted to the editable layout: their old files are replaced or deleted (the user asked for it).
        foreach (var entity in updated.Entities.Where(e => e.IsEditable))
        {
            if (current.Entities.FirstOrDefault(e => e.Name == entity.Name) is not { IsEditable: false } legacy)
            {
                continue;
            }

            var entityFiles = ServiceGenerator.GenerateEntity(updated, entity, options).ToDictionary(f => f.Path, StringComparer.Ordinal);
            foreach (var path in ServiceGenerator.LegacyEntityPaths(current, legacy))
            {
                if (Read(path) is not { } previous)
                {
                    continue;
                }

                handled.Add(path);
                if (!entityFiles.TryGetValue(path, out var file))
                {
                    changes.Add(new FileChange(path, ChangeKind.Delete, string.Empty, previous));
                }
                else if (!KeptOnConversion(path))
                {
                    changes.Add(new FileChange(path, ChangeKind.Replace, file.Content, previous));
                }
            }
        }

        // 2. Generated files: *.g.cs always rewritten; the user's files created only the first time they appear.
        foreach (var file in files)
        {
            if (handled.Contains(file.Path) || file.Path == ServiceGenerator.DefinitionFileName)
            {
                continue;
            }

            var previous = Read(file.Path);
            if (file.Path.EndsWith(".g.cs", StringComparison.Ordinal))
            {
                if (previous != file.Content)
                {
                    changes.Add(new FileChange(file.Path, previous is null ? ChangeKind.Create : ChangeKind.Regenerate, file.Content, previous));
                }
            }
            else if (previous is null && IsCreateOnce(file.Path, service) && !oldPaths.Contains(file.Path))
            {
                // Not in the previous generation = new entity (or newly enabled part). A file of an existing
                // entity that the user deleted on purpose (e.g. an event handler) is not brought back.
                changes.Add(new FileChange(file.Path, ChangeKind.Create, file.Content, null));
            }
        }

        // 3. Files no longer generated (removed entities).
        foreach (var path in oldPaths.Where(p => !newPaths.Contains(p)).Order(StringComparer.Ordinal))
        {
            if (Read(path) is not { } previous)
            {
                continue;
            }

            if (path.EndsWith(".g.cs", StringComparison.Ordinal))
            {
                changes.Add(new FileChange(path, ChangeKind.Delete, string.Empty, previous));
            }
            else if (IsCreateOnce(path, service))
            {
                DeleteOwn(path, previous);
            }
        }

        foreach (var legacy in current.Entities.Where(e => !e.IsEditable && !updated.Entities.Any(u => u.Name == e.Name)))
        {
            foreach (var path in ServiceGenerator.LegacyEntityPaths(current, legacy))
            {
                if (Read(path) is { } previous)
                {
                    DeleteOwn(path, previous);
                }
            }
        }

        if (ownLeft.Count > 0)
        {
            warnings.Add(
                "Estos archivos tuyos son de entidades eliminadas y MSG no los borra: probablemente ya no compilen. " +
                "Bórralos tú o marca \"borrar también mis archivos\":\n" + string.Join("\n", ownLeft.Select(p => "      " + p)));
        }

        // 4. Project files and appsettings: only missing entries are added.
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

        // 5. msg.json. msgVersion stays as it was: editing entities must not silently upgrade the Msg.* packages.
        var definition = ServiceGenerator.Serialize(updated with { MsgVersion = current.MsgVersion });
        var definitionPrevious = Read(ServiceGenerator.DefinitionFileName);
        if (definition != definitionPrevious || changes.Count > 0)
        {
            changes.Add(new FileChange(ServiceGenerator.DefinitionFileName, ChangeKind.Regenerate, definition, definitionPrevious));
        }

        warnings.AddRange(CheckReferences(directory, current, updated));
        warnings.AddRange(CheckWiring(directory, service, current.Features));

        return new ChangePlan(directory, changes, warnings);

        string? Read(string relative)
        {
            var target = Path.Combine(directory, relative);
            return File.Exists(target) ? File.ReadAllText(target) : null;
        }

        void DeleteOwn(string path, string previous)
        {
            if (deleteOwnFiles)
            {
                changes.Add(new FileChange(path, ChangeKind.Delete, string.Empty, previous));
            }
            else
            {
                ownLeft.Add(path);
            }
        }

        void AddMerge(string relative, Func<string, string, string?> merge)
        {
            var rendered = files.FirstOrDefault(f => f.Path == relative);
            if (rendered is null || Read(relative) is not { } previous)
            {
                return;
            }

            if (merge(previous, rendered.Content) is { } merged)
            {
                changes.Add(new FileChange(relative, ChangeKind.Merge, merged, previous));
            }
        }
    }

    /// <summary>On conversion these keep their path and content (they don't depend on the properties).</summary>
    private static bool KeptOnConversion(string path) =>
        path.EndsWith("Events.cs", StringComparison.Ordinal)
        || path.EndsWith("Repository.cs", StringComparison.Ordinal)
        || path.EndsWith("EventHandlers.cs", StringComparison.Ordinal);

    /// <summary>Removed or renamed properties still used in the user's files of that entity.</summary>
    private static List<string> CheckReferences(string root, ServiceDefinition current, ServiceDefinition updated)
    {
        var warnings = new List<string>();
        foreach (var entity in updated.Entities.Where(e => e.IsEditable))
        {
            if (current.Entities.FirstOrDefault(e => e.Name == entity.Name) is not { IsEditable: true } before)
            {
                continue;
            }

            var gone = before.Properties.Where(p => !entity.Properties.Any(n => n.Name == p.Name)).ToList();
            if (gone.Count == 0)
            {
                continue;
            }

            // Renames of this change: new entries in pendingRenames (stored with the original name, A → C after A → B → C).
            var renamedTo = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rename in (updated.PendingRenames ?? []).Where(r => r.Entity == entity.Name && !(current.PendingRenames ?? []).Contains(r)))
            {
                var name = before.Properties.Any(p => p.Name == rename.From)
                    ? rename.From
                    : (current.PendingRenames ?? []).FirstOrDefault(r => r.Entity == entity.Name && r.From == rename.From)?.To;
                if (name is not null)
                {
                    renamedTo[name] = rename.To;
                }
            }

            var userFiles = ServiceGenerator.GenerateEntity(updated, entity)
                .Select(f => f.Path)
                .Where(p => !p.EndsWith(".g.cs", StringComparison.Ordinal) && File.Exists(Path.Combine(root, p)))
                .ToList();

            foreach (var property in gone)
            {
                var pattern = new System.Text.RegularExpressions.Regex($@"\b{property.Name}\b");
                var usedIn = userFiles.Where(p => pattern.IsMatch(File.ReadAllText(Path.Combine(root, p)))).ToList();
                var change = Renamed(property.Name) is { } to ? $"se renombra a {to}" : "se elimina";

                if (usedIn.Count > 0)
                {
                    warnings.Add($"{entity.Name}.{property.Name} {change}, pero tu código la usa en:\n" +
                                 string.Join("\n", usedIn.Select(p => "      " + p)) + "\n    Actualízalo para que compile.");
                }

                if (Renamed(property.Name) is null && updated.Persistence is not null)
                {
                    warnings.Add($"{entity.Name}.{property.Name} se elimina: la próxima migración borrará su columna y los datos que tenga.");
                }
            }

            string? Renamed(string name) => renamedTo.TryGetValue(name, out var to) ? to : null;
        }

        return warnings;
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
