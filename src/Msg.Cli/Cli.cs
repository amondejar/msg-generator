using Msg.Core;
using Msg.Core.Generation;
using Msg.Core.Model;
using Msg.Core.Templating;
using Msg.Studio;
using Msg.Studio.State;

namespace Msg.Cli;

/// <summary>
/// Hand-rolled argument parsing: Phase 1 has a single command and we want zero dependencies.
/// Move to System.CommandLine when the command set grows (add, regen, upgrade...).
/// </summary>
internal static partial class Cli
{
    private const string Usage = """
        MSG {0} — generador de microservicios .NET

        Uso:
          msg new <Nombre> [opciones]        Crea un servicio nuevo
          msg new --from <msg.json> [opc.]   Crea un servicio a partir de una definición
          msg add entity <Nombre> --props "Campo:tipo, ..." [--plural <P>] [--db postgres|sqlserver] [--path <carpeta>]
                                             Añade una entidad con CRUD, EF Core y tests
          msg entity list [--path <carpeta>]          Entidades del servicio y sus propiedades
          msg entity update <Entidad> --props "..." [--rename Viejo=Nuevo]... [--path] [--dry-run]
                                             Cambia las propiedades (las que falten se eliminan)
          msg entity remove <Entidad> [--delete-own-files] [--path] [--dry-run]
                                             Elimina una entidad (tus archivos solo con --delete-own-files)
          msg entity convert <Entidad> [--path] [--dry-run]
                                             Pasa una entidad anterior a 0.10 al formato editable
          msg feature list|on|off <feature>... [--path <carpeta>] [--dry-run]
                                             Activa o desactiva features en un servicio existente
                                             (observability, openApi, resilience, auth, tests, docker, ci)
          msg feature convert [--path] [--dry-run]
                                             Pasa un Program.cs anterior a 0.12 a MsgWiring.g.cs
          msg messaging on --broker kafka|inMemory [--path] [--dry-run]
          msg messaging off [--path] [--dry-run]
                                             Eventos de integración entre servicios (outbox, inbox, reintentos)
          msg events on|off <Entidad>... [--path] [--dry-run]
                                             Publica <Entidad>Created/Updated/Deleted para otros servicios
          msg migration review [--path <carpeta>] [--dry-run]
                                             Revisa la última migración de EF Core: aplica los renombrados
                                             (RenameColumn) y avisa de lo que borra datos
          msg aspire [--name <Sistema>] [--path <carpeta>]
                                             AppHost de Aspire con todos los servicios de la carpeta
          msg studio [--path <carpeta>] [--port 5150] [--no-browser]
                                             Editor visual en el navegador
          msg templates list [--path <carpeta>]        Plantillas de MSG y del equipo (.msg/templates)
          msg templates eject <set>[/<archivo>] [--path <carpeta>] [--force]
                                             Copia plantillas a .msg/templates para personalizarlas
          msg upgrade [--path <carpeta>] [--to <versión>]
                                             Actualiza los paquetes Msg.* de un servicio existente
          msg --version

        Opciones:
          -o, --output <carpeta>   Carpeta de salida (por defecto ./<Nombre>)
              --force              Sobrescribe si la carpeta no está vacía
              --dry-run            Muestra los archivos sin escribirlos
              --architecture <a>   vertical-slice (por defecto) o clean-ddd
              --package-source <carpeta>
                                   Usa paquetes Msg.* empaquetados en local (antes de publicarlos
                                   en nuget.org). Genera un nuget.config con source mapping.

        Features (activas por defecto salvo auth):
          --[no-]observability     OpenTelemetry: trazas, métricas y logs
          --[no-]openapi           Documento OpenAPI + Scalar (/scalar)
          --[no-]resilience        Reintentos/timeout/circuit breaker en HttpClient
          --[no-]auth              JWT bearer, autenticado por defecto      (off)
          --[no-]tests             Tests de integración
          --[no-]docker            Dockerfile
          --[no-]ci                GitHub Actions

        Siempre incluido: estructura de solución, Central Package Management, health checks,
        ProblemDetails, correlation id, logs estructurados y configuración validada.
        Lo común llega como paquetes Msg.ServiceDefaults.*: 'msg upgrade' lo actualiza sin regenerar.

        Tipos de --props: string, int, long, decimal, bool, guid, date, datetime. Añade ? si es opcional.
          msg add entity Product --props "Name:string, Price:decimal, Description:string?"
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        if (args.Length > 0 && args[0] == "studio")
        {
            return await Studio(args[1..], output).ConfigureAwait(false);
        }

        return Run(args, output);
    }

    public static int Run(string[] args, TextWriter output)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine(Usage, MsgInfo.Version);
            return 0;
        }

        if (args[0] is "--version" or "-v")
        {
            output.WriteLine(MsgInfo.Version);
            return 0;
        }

        return args[0] switch
        {
            "new" => New(args[1..], output),
            "upgrade" => Upgrade(args[1..], output),
            "add" => Add(args[1..], output),
            "entity" => Entity(args[1..], output),
            "migration" => Migration(args[1..], output),
            "feature" => Feature(args[1..], output),
            "messaging" => Messaging(args[1..], output),
            "events" => Events(args[1..], output),
            "aspire" => Aspire(args[1..], output),
            "templates" => Templates(args[1..], output),
            _ => throw new MsgException($"Comando desconocido '{args[0]}'. Usa 'msg --help'."),
        };
    }

    private static int New(string[] args, TextWriter output)
    {
        string? name = null, outputDir = null, from = null, packageSource = null, architecture = null;
        bool force = false, dryRun = false;
        var overrides = new List<(string Feature, bool Enabled)>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-o" or "--output":
                    outputDir = Value(args, ref i, arg);
                    break;
                case "--from":
                    from = Value(args, ref i, arg);
                    break;
                case "--package-source":
                    packageSource = Value(args, ref i, arg);
                    if (!Directory.Exists(packageSource))
                    {
                        throw new MsgException($"La carpeta de paquetes '{packageSource}' no existe. Créala con 'dotnet pack Msg.slnx -c Release -o <carpeta>'.");
                    }

                    break;
                case "--force":
                    force = true;
                    break;
                case "--architecture" or "-a":
                    architecture = Architectures.Validate(Value(args, ref i, arg).ToLowerInvariant());
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "-h" or "--help":
                    output.WriteLine(Usage, MsgInfo.Version);
                    return 0;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        var enabled = !arg.StartsWith("--no-", StringComparison.Ordinal);
                        var feature = enabled ? arg[2..] : arg[5..];
                        overrides.Add((ResolveFeature(feature, arg), enabled));
                    }
                    else if (name is null)
                    {
                        name = arg;
                    }
                    else
                    {
                        throw new MsgException($"Argumento inesperado '{arg}'.");
                    }

                    break;
            }
        }

        var definition = from is not null
            ? LoadDefinition(from, name)
            : new ServiceDefinition
            {
                Service = new ServiceInfo
                {
                    Name = ServiceName.Normalize(name ?? throw new MsgException("Falta el nombre del servicio: msg new <Nombre>")),
                },
            };

        var features = definition.Features;
        foreach (var (feature, enabled) in overrides)
        {
            features = features.With(feature, enabled);
        }

        definition = definition with
        {
            Features = features,
            Architecture = architecture ?? definition.Architecture,
        };

        var serviceName = definition.Service.Name;
        outputDir ??= serviceName;
        var team = TeamTemplates.Find(Path.GetDirectoryName(Path.GetFullPath(outputDir))!);
        var files = ServiceGenerator.Generate(definition, new GenerationOptions { LocalPackageSource = packageSource, Team = team });

        if (dryRun)
        {
            foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                output.WriteLine(file.Path);
            }

            return 0;
        }

        FileWriter.Write(files, outputDir, force);
        PrintSummary(output, definition, files.Count, outputDir);
        PrintTeam(output, team);
        return 0;
    }

    private static int Add(string[] args, TextWriter output)
    {
        if (args.Length == 0 || args[0] != "entity")
        {
            throw new MsgException("Uso: msg add entity <Nombre> --props \"Campo:tipo, ...\"");
        }

        string? name = null, props = null, plural = null, db = null;
        var path = ".";

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--props":
                    props = Value(args, ref i, args[i]);
                    break;
                case "--plural":
                    plural = Value(args, ref i, args[i]);
                    break;
                case "--db":
                    db = Value(args, ref i, args[i]).ToLowerInvariant();
                    break;
                case "--path":
                    path = Value(args, ref i, args[i]);
                    break;
                default:
                    if (name is null && !args[i].StartsWith('-'))
                    {
                        name = args[i];
                        break;
                    }

                    throw new MsgException($"Argumento inesperado '{args[i]}'.");
            }
        }

        var result = EntityAdder.Add(new AddEntityRequest
        {
            ServiceDirectory = path,
            Name = name ?? throw new MsgException("Falta el nombre de la entidad: msg add entity <Nombre> --props \"...\""),
            Properties = props ?? throw new MsgException("Falta --props \"Campo:tipo, ...\" (p. ej. \"Name:string, Price:decimal\")."),
            Plural = plural,
            Provider = db,
        });

        var entity = result.Entity;
        if (result.TeamTemplates is { } teamRoot)
        {
            output.WriteLine($"  plantillas del equipo: {teamRoot}");
        }

        output.WriteLine($"✔ Entidad {entity.Name} añadida ({result.Provider}) → /api/v1/{ServiceName.ToKebab(entity.Plural)}");
        foreach (var file in result.Created)
        {
            output.WriteLine($"  + {file}");
        }

        foreach (var file in result.Updated)
        {
            output.WriteLine($"  ~ {file}");
        }

        foreach (var warning in result.Warnings)
        {
            output.WriteLine();
            output.WriteLine($"⚠ {warning}");
        }

        output.WriteLine();
        output.WriteLine("Siguientes pasos:");
        output.WriteLine("  dotnet test                                   # los tests usan SQLite en memoria");
        output.WriteLine("  docker compose up -d                          # base de datos local");
        var definition = ServiceGenerator.Deserialize(File.ReadAllText(Path.Combine(path, ServiceGenerator.DefinitionFileName)));
        var service = definition.Service.Name;
        var efProjects = definition.Architecture == Architectures.CleanDdd
            ? $"--project src/{service}.Infrastructure --startup-project src/{service}.Api"
            : $"--project src/{service}.Api";
        output.WriteLine($"  dotnet ef migrations add Add{entity.Name} {efProjects}");
        output.WriteLine($"  dotnet ef database update {efProjects}");
        return 0;
    }

    private static async Task<int> Studio(string[] args, TextWriter output)
    {
        var path = ".";
        var port = 5150;
        var openBrowser = true;
        string? packageSource = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--path":
                    path = Value(args, ref i, args[i]);
                    break;
                case "--port":
                    port = int.TryParse(Value(args, ref i, args[i]), out var p) && p is > 0 and < 65536
                        ? p
                        : throw new MsgException("--port necesita un número entre 1 y 65535.");
                    break;
                case "--no-browser":
                    openBrowser = false;
                    break;
                case "--package-source":
                    packageSource = Path.GetFullPath(Value(args, ref i, args[i]));
                    break;
                default:
                    throw new MsgException($"Argumento inesperado '{args[i]}'. Uso: msg studio [--path <carpeta>] [--port 5150] [--no-browser]");
            }
        }

        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
        {
            throw new MsgException($"No existe la carpeta '{full}'.");
        }

        // Opened on a service folder: the system is its parent, and that service is opened.
        var isService = File.Exists(Path.Combine(full, ServiceGenerator.DefinitionFileName));

        // "dotnet run --project .../Msg.Cli" starts in the project folder, not where you typed it:
        // never turn a .NET project folder into a system of services.
        if (!isService && Directory.EnumerateFiles(full, "*.csproj").Any())
        {
            throw new MsgException(
                $"'{full}' es un proyecto .NET, no una carpeta de servicios (¿lo lanzaste con dotnet run?). " +
                "Indica la carpeta con --path, p. ej.: msg studio --path C:\\Repos\\servicios");
        }
        var options = new StudioOptions
        {
            Workspace = isService ? Path.GetDirectoryName(full)! : full,
            OpenService = isService ? Path.GetFileName(full) : null,
            PackageSource = packageSource,
        };

        await StudioServer.RunAsync(options, port, openBrowser, output).ConfigureAwait(false);
        return 0;
    }

    private static void PrintTeam(TextWriter output, TeamTemplates? team)
    {
        if (team is null)
        {
            return;
        }

        var custom = team.CustomizedFiles();
        output.WriteLine($"  plantillas del equipo: {team.Root} ({custom.Count} archivo(s), {team.Tokens.Count} token(s))");
    }

    private static int Templates(string[] args, TextWriter output)
    {
        if (args.Length == 0 || args[0] is not ("list" or "eject"))
        {
            throw new MsgException("Uso: msg templates list | msg templates eject <set>[/<archivo>]  (sets: " + string.Join(", ", TemplateSource.Sets) + ")");
        }

        var path = ".";
        var force = false;
        string? target = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--path":
                    path = Value(args, ref i, args[i]);
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    if (target is null && !args[i].StartsWith('-'))
                    {
                        target = args[i].Replace('\\', '/').Trim('/');
                        break;
                    }

                    throw new MsgException($"Argumento inesperado '{args[i]}'.");
            }
        }

        var team = TeamTemplates.Find(path);
        return args[0] == "list" ? ListTemplates(output, team) : EjectTemplates(output, team, path, target, force);
    }

    private static int ListTemplates(TextWriter output, TeamTemplates? team)
    {
        output.WriteLine(team is null
            ? "Sin plantillas del equipo (crea una con: msg templates eject <set>/<archivo>)."
            : $"Plantillas del equipo: {team.Root}");
        if (team is not null && team.Tokens.Count > 0)
        {
            output.WriteLine($"  tokens: {string.Join(", ", team.Tokens.Keys.Select(k =>
                "{{" + k + "}}" + (ServiceGenerator.ReservedTokens.Contains(k) ? " (ignorado: es de MSG)" : string.Empty)))}");
        }

        foreach (var set in TemplateSource.Sets)
        {
            output.WriteLine();
            output.WriteLine($"{set}/");
            var custom = team?.FilesFor(set) ?? new Dictionary<string, string>();
            var embedded = TemplateSource.LoadEmbedded(set).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

            foreach (var file in embedded.Union(custom.Keys).Order(StringComparer.Ordinal))
            {
                var mark = !custom.TryGetValue(file, out var content) ? "     "
                    : !embedded.Contains(file) ? "[+]  "
                    : string.IsNullOrWhiteSpace(content) ? "[-]  "
                    : "[~]  ";
                output.WriteLine($"  {mark}{file}");
            }
        }

        output.WriteLine();
        output.WriteLine("[~] personalizada por el equipo  [+] añadida por el equipo  [-] desactivada (archivo vacío)");
        return 0;
    }

    private static int EjectTemplates(TextWriter output, TeamTemplates? team, string path, string? target, bool force)
    {
        if (string.IsNullOrEmpty(target))
        {
            throw new MsgException("Indica qué copiar: msg templates eject service/src/__Service__.Api/Program.cs  (o un set entero: msg templates eject service)");
        }

        var slash = target.IndexOf('/', StringComparison.Ordinal);
        var set = slash < 0 ? target : target[..slash];
        var file = slash < 0 ? null : target[(slash + 1)..];

        if (!TemplateSource.Sets.Contains(set))
        {
            throw new MsgException($"Set desconocido '{set}'. Sets: {string.Join(", ", TemplateSource.Sets)}.");
        }

        var selected = TemplateSource.LoadEmbedded(set).Where(f => file is null || f.Path == file).ToList();
        if (selected.Count == 0)
        {
            throw new MsgException($"No existe la plantilla '{target}'. Míralas con: msg templates list");
        }

        var msgFolder = team?.Root ?? Path.Combine(Path.GetFullPath(path), TeamTemplates.FolderName);
        var config = Path.Combine(msgFolder, TeamTemplates.ConfigFile);
        if (!File.Exists(config))
        {
            Directory.CreateDirectory(msgFolder);
            File.WriteAllText(config, """
                {
                  // Tokens of your team, usable as {{Company}} in any template under .msg/templates.
                  "tokens": {
                    "Company": "MiEmpresa"
                  }
                }

                """);
            output.WriteLine($"  + {Path.GetRelativePath(Path.GetFullPath(path), config)}");
        }

        foreach (var template in selected)
        {
            var destination = Path.Combine(msgFolder, TeamTemplates.TemplatesFolder, set, template.Path);
            if (File.Exists(destination) && !force)
            {
                output.WriteLine($"  = {set}/{template.Path} (ya existe; --force para sobrescribir)");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, template.Content);
            output.WriteLine($"  + {Path.GetRelativePath(Path.GetFullPath(path), destination)}");
        }

        output.WriteLine();
        output.WriteLine("Edítalas y haz commit de .msg: msg new, msg add entity y msg studio las usarán en todo el repo.");
        output.WriteLine("Un archivo vacío desactiva esa plantilla; un archivo nuevo se añade a lo que genera MSG.");
        return 0;
    }

    private static int Aspire(string[] args, TextWriter output)
    {
        var root = ".";
        string? name = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--path":
                    root = Value(args, ref i, args[i]);
                    break;
                case "--name":
                    name = Value(args, ref i, args[i]);
                    break;
                default:
                    throw new MsgException($"Argumento inesperado '{args[i]}'. Uso: msg aspire [--name <Sistema>] [--path <carpeta>]");
            }
        }

        // A service folder is not a system: run it from the folder that contains the services.
        if (File.Exists(Path.Combine(root, ServiceGenerator.DefinitionFileName)))
        {
            throw new MsgException("Esta carpeta es un servicio. Ejecuta 'msg aspire' en la carpeta que contiene tus servicios (o usa --path).");
        }

        var appHostName = ServiceName.Normalize(name ?? Path.GetFileName(Path.GetFullPath(root)));
        var services = AspireGenerator.Discover(root);
        var result = AspireGenerator.Generate(root, appHostName, services);

        output.WriteLine($"✔ {result.AppHostName} con {services.Count} servicio(s)");
        foreach (var service in services)
        {
            var db = service.Definition.Persistence?.Provider is { } provider ? $" + {provider} ({service.ResourceName}-db)" : string.Empty;
            output.WriteLine($"  · {service.ResourceName,-20} {service.RelativeDirectory}{db}");
        }

        foreach (var file in result.Created)
        {
            output.WriteLine($"  + {file}");
        }

        foreach (var file in result.Updated)
        {
            output.WriteLine($"  ~ {file}");
        }

        output.WriteLine();
        output.WriteLine("Siguientes pasos (necesitas Docker o Podman para las bases de datos):");
        output.WriteLine("  aspire run                                    # o: dotnet run --project " + result.AppHostName);
        output.WriteLine("  El dashboard muestra servicios, trazas, métricas y logs de todos ellos.");
        return 0;
    }

    private static int Upgrade(string[] args, TextWriter output)
    {
        var path = ".";
        var target = MsgInfo.Version;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--path":
                    path = Value(args, ref i, args[i]);
                    break;
                case "--to":
                    target = Value(args, ref i, args[i]);
                    if (!SemVer().IsMatch(target))
                    {
                        throw new MsgException($"'{target}' no es una versión válida (p. ej. 0.3.0).");
                    }

                    break;
                default:
                    throw new MsgException($"Argumento inesperado '{args[i]}'. Uso: msg upgrade [--path <carpeta>] [--to <versión>]");
            }
        }

        var result = ServiceUpgrader.Upgrade(path, target);

        if (result.FromVersion == result.ToVersion)
        {
            output.WriteLine($"✔ Ya está en MSG {result.ToVersion} ({string.Join(", ", result.Packages)}).");
            return 0;
        }

        output.WriteLine($"✔ MSG {result.FromVersion} → {result.ToVersion}");
        foreach (var package in result.Packages)
        {
            output.WriteLine($"  {package} {result.ToVersion}");
        }

        foreach (var hint in result.Hints)
        {
            output.WriteLine();
            output.WriteLine($"ℹ {hint}");
        }

        output.WriteLine();
        output.WriteLine("Siguientes pasos: dotnet build && dotnet test, y revisa las notas de la versión.");
        return 0;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial System.Text.RegularExpressions.Regex SemVer();

    private static ServiceDefinition LoadDefinition(string path, string? nameOverride)
    {
        if (!File.Exists(path))
        {
            throw new MsgException($"No existe el archivo '{path}'.");
        }

        var definition = ServiceGenerator.Deserialize(File.ReadAllText(path));
        return nameOverride is null
            ? definition
            : definition with { Service = definition.Service with { Name = ServiceName.Normalize(nameOverride) } };
    }

    private static int Entity(string[] args, TextWriter output)
    {
        const string usage = "Uso: msg entity list|update|remove|convert <Entidad> [opciones]. Usa 'msg --help'.";
        if (args.Length == 0)
        {
            throw new MsgException(usage);
        }

        var action = args[0];
        string? name = null, props = null;
        var path = ".";
        bool dryRun = false, deleteOwn = false;
        var renames = new List<PropertyRename>();

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--props" when action == "update":
                    props = Value(args, ref i, args[i]);
                    break;
                case "--rename" when action == "update":
                    var pair = Value(args, ref i, args[i]).Split('=', StringSplitOptions.TrimEntries);
                    if (pair.Length != 2 || pair[0].Length == 0 || pair[1].Length == 0)
                    {
                        throw new MsgException("--rename espera Viejo=Nuevo (p. ej. --rename Name=Title).");
                    }

                    renames.Add(new PropertyRename { Entity = string.Empty, From = pair[0], To = pair[1] });
                    break;
                case "--delete-own-files" when action == "remove":
                    deleteOwn = true;
                    break;
                case "--path":
                    path = Value(args, ref i, args[i]);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    if (name is null && !args[i].StartsWith('-'))
                    {
                        name = args[i];
                        break;
                    }

                    throw new MsgException($"Argumento inesperado '{args[i]}'.");
            }
        }

        var current = ServicePlanner.Load(path);
        if (action == "list")
        {
            ListEntities(output, current);
            return 0;
        }

        var entityName = name ?? throw new MsgException($"Falta el nombre de la entidad. {usage}");
        var updated = action switch
        {
            "update" => ServicePlanner.UpdateEntity(
                current,
                entityName,
                PropertyTypes.Parse(props ?? throw new MsgException("Falta --props con la lista completa de propiedades (las que no estén se eliminan).")),
                renames),
            "remove" => ServicePlanner.RemoveEntity(current, entityName),
            "convert" => ServicePlanner.ConvertEntity(current, entityName),
            _ => throw new MsgException(usage),
        };

        var team = TeamTemplates.Find(path);
        var plan = ServicePlanner.Plan(path, current, updated, new GenerationOptions { Team = team }, deleteOwn);
        PrintPlan(output, plan, dryRun);

        if (!dryRun && plan.Changes.Count > 0)
        {
            plan.Apply();
            if (action == "convert")
            {
                output.WriteLine();
                output.WriteLine("La base de datos no cambia. Si habías personalizado los archivos reemplazados, pasa esos cambios a las clases partial.");
            }
            else if (current.Persistence is not null)
            {
                var service = current.Service.Name;
                var efProjects = current.Architecture == Architectures.CleanDdd
                    ? $"--project src/{service}.Infrastructure --startup-project src/{service}.Api"
                    : $"--project src/{service}.Api";
                output.WriteLine();
                output.WriteLine("Siguiente paso: crea la migración y revísala (o hazlo desde el Studio, que hace las dos cosas):");
                output.WriteLine($"  dotnet ef migrations add <Nombre> {efProjects}");
                output.WriteLine($"  msg migration review{(path == "." ? string.Empty : $" --path {path}")}");
            }
        }

        return 0;
    }

    private static int Feature(string[] args, TextWriter output)
    {
        const string usage = "Uso: msg feature list|on|off <feature>... | msg feature convert  [--path <carpeta>] [--dry-run]";
        if (args.Length == 0 || args[0] is not ("list" or "on" or "off" or "convert"))
        {
            throw new MsgException(usage);
        }

        var action = args[0];
        var path = ".";
        var dryRun = false;
        var names = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--path":
                    path = Value(args, ref i, args[i]);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    if (args[i].StartsWith('-') || action is "list" or "convert")
                    {
                        throw new MsgException($"Argumento inesperado '{args[i]}'. {usage}");
                    }

                    names.Add(ResolveFeature(args[i], args[i]));
                    break;
            }
        }

        var current = ServicePlanner.Load(path);
        var team = TeamTemplates.Find(path);
        var options = new GenerationOptions { Team = team };

        if (action == "list")
        {
            foreach (var name in FeatureSet.Names)
            {
                output.WriteLine($"  [{(current.Features.IsEnabled(name) ? "x" : " ")}] {name}");
            }

            if (!ServicePlanner.UsesMsgWiring(path, current))
            {
                output.WriteLine();
                output.WriteLine("Program.cs es anterior a MSG 0.12: para cambiar features, primero: msg feature convert --dry-run");
            }

            return 0;
        }

        ChangePlan plan;
        if (action == "convert")
        {
            plan = ServicePlanner.ConvertWiring(path, current, options);
        }
        else
        {
            if (names.Count == 0)
            {
                throw new MsgException($"Indica qué features. Válidas: {string.Join(", ", FeatureSet.Names)}.");
            }

            var features = names.Aggregate(current.Features, (set, name) => set.With(name, action == "on"));
            plan = ServicePlanner.Plan(path, current, ServicePlanner.SetFeatures(path, current, features), options);
        }

        PrintPlan(output, plan, dryRun);
        if (!dryRun && plan.Changes.Count > 0)
        {
            plan.Apply();
        }

        return 0;
    }

    private static int Messaging(string[] args, TextWriter output)
    {
        const string usage = "Uso: msg messaging on --broker kafka|inMemory | msg messaging off  [--path <carpeta>] [--dry-run]";
        if (args.Length == 0 || args[0] is not ("on" or "off"))
        {
            throw new MsgException(usage);
        }

        var path = ".";
        var dryRun = false;
        string? broker = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--broker" when args[0] == "on":
                    broker = Value(args, ref i, args[i]);
                    break;
                case "--path":
                    path = Value(args, ref i, args[i]);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    throw new MsgException($"Argumento inesperado '{args[i]}'. {usage}");
            }
        }

        if (args[0] == "on" && broker is null)
        {
            throw new MsgException($"Falta --broker. Válidos: {string.Join(", ", MessagingSettings.Brokers)}.");
        }

        var current = ServicePlanner.Load(path);
        var updated = ServicePlanner.SetMessaging(path, current, broker);
        var plan = ServicePlanner.Plan(path, current, updated, new GenerationOptions { Team = TeamTemplates.Find(path) });
        PrintPlan(output, plan, dryRun);
        if (!dryRun && plan.Changes.Count > 0)
        {
            plan.Apply();
            if (updated.Messaging is not null && current.Messaging is null && current.Persistence is not null)
            {
                output.WriteLine();
                output.WriteLine("Siguiente paso: crea la migración con las tablas msg_outbox y msg_inbox (Studio, o dotnet ef migrations add AddMessaging).");
            }

            if (updated.Messaging?.Broker == MessagingSettings.Kafka)
            {
                output.WriteLine("Para ejecutarlo con Kafka, regenera el AppHost (msg aspire, o desde el Studio): añade Kafka y Kafka UI.");
            }
        }

        return 0;
    }

    private static int Events(string[] args, TextWriter output)
    {
        const string usage = "Uso: msg events on|off <Entidad>... [--path <carpeta>] [--dry-run]";
        if (args.Length < 2 || args[0] is not ("on" or "off"))
        {
            throw new MsgException(usage);
        }

        var path = ".";
        var dryRun = false;
        var names = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--path":
                    path = Value(args, ref i, args[i]);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        throw new MsgException($"Argumento inesperado '{args[i]}'. {usage}");
                    }

                    names.Add(args[i]);
                    break;
            }
        }

        var current = ServicePlanner.Load(path);
        var updated = names.Aggregate(current, (definition, name) => ServicePlanner.SetPublishEvents(definition, name, args[0] == "on"));
        var plan = ServicePlanner.Plan(path, current, updated, new GenerationOptions { Team = TeamTemplates.Find(path) });
        PrintPlan(output, plan, dryRun);
        if (!dryRun && plan.Changes.Count > 0)
        {
            plan.Apply();
        }

        return 0;
    }

    private static int Migration(string[] args, TextWriter output)
    {
        if (args.Length == 0 || args[0] != "review")
        {
            throw new MsgException("Uso: msg migration review [--path <carpeta>] [--dry-run]");
        }

        var path = ".";
        var dryRun = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--path":
                    path = Value(args, ref i, args[i]);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    throw new MsgException($"Argumento inesperado '{args[i]}'.");
            }
        }

        var review = MigrationReviewer.ReviewLatest(path, apply: !dryRun);
        output.WriteLine($"Migración {review.Name} ({review.Path})");
        if (review.Findings.Count == 0)
        {
            output.WriteLine("  ✔ No borra datos ni cambia columnas existentes.");
        }

        foreach (var finding in review.Findings)
        {
            var symbol = finding.Level switch
            {
                FindingLevel.Danger => "✖ BORRA DATOS:",
                FindingLevel.Warning => "⚠",
                _ => "✔",
            };
            output.WriteLine($"  {symbol} {finding.Message}");
        }

        if (review.Changed)
        {
            output.WriteLine(dryRun
                ? "  (--dry-run: la migración no se ha modificado)"
                : "  La migración se ha corregido; los renombrados ya no están pendientes en msg.json.");
        }

        if (review.DeletesData)
        {
            output.WriteLine();
            output.WriteLine("Si no quieres perder esos datos, deshaz la migración (dotnet ef migrations remove) antes de aplicarla.");
        }

        return 0;
    }

    private static void ListEntities(TextWriter output, ServiceDefinition definition)
    {
        if (definition.Entities.Count == 0)
        {
            output.WriteLine("El servicio no tiene entidades. Añade una con: msg add entity <Nombre> --props \"...\"");
            return;
        }

        foreach (var entity in definition.Entities)
        {
            var props = string.Join(", ", entity.Properties.Select(p => $"{p.Name}:{p.Type}{(p.Nullable ? "?" : string.Empty)}"));
            output.WriteLine($"{entity.Name} ({entity.Plural}){(entity.IsEditable ? string.Empty : "  [anterior a 0.10: msg entity convert " + entity.Name + "]")}");
            output.WriteLine($"  --props \"{props}\"");
        }

        foreach (var rename in definition.PendingRenames ?? [])
        {
            output.WriteLine($"  renombrado pendiente de migración: {rename.Entity}.{rename.From} → {rename.To}");
        }
    }

    private static void PrintPlan(TextWriter output, ChangePlan plan, bool dryRun)
    {
        if (plan.Changes.Count == 0)
        {
            output.WriteLine("Nada que cambiar: el código ya coincide con msg.json.");
            return;
        }

        output.WriteLine(dryRun ? "Cambios (--dry-run, no se escribe nada):" : "✔ Cambios aplicados:");
        foreach (var change in plan.Changes)
        {
            var symbol = change.Kind switch
            {
                ChangeKind.Create => "+",
                ChangeKind.Regenerate or ChangeKind.Merge => "~",
                ChangeKind.Replace => "!",
                _ => "-",
            };
            var note = change.TouchesUserCode ? "   (archivo tuyo)" : string.Empty;
            output.WriteLine($"  {symbol} {change.Path}{note}");
        }

        foreach (var warning in plan.Warnings)
        {
            output.WriteLine();
            output.WriteLine($"⚠ {warning}");
        }
    }

    private static string ResolveFeature(string input, string arg) =>
        FeatureSet.Names.FirstOrDefault(n => n.Equals(input, StringComparison.OrdinalIgnoreCase))
        ?? throw new MsgException($"Opción desconocida '{arg}'. Usa 'msg --help'.");

    private static string Value(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
        {
            throw new MsgException($"La opción {option} necesita un valor.");
        }

        return args[++i];
    }

    private static void PrintSummary(TextWriter output, ServiceDefinition definition, int fileCount, string outputDir)
    {
        var name = definition.Service.Name;
        var features = definition.Features;
        var on = FeatureSet.Names.Where(features.IsEnabled).DefaultIfEmpty("(solo la base común)");
        var off = FeatureSet.Names.Where(f => !features.IsEnabled(f)).ToList();

        output.WriteLine($"✔ Servicio {name} creado en {Path.GetFullPath(outputDir)} ({fileCount} archivos)");
        output.WriteLine($"  arquitectura: {definition.Architecture}");
        output.WriteLine($"  features: {string.Join(", ", on)}");
        if (off.Count > 0)
        {
            output.WriteLine($"  desactivadas: {string.Join(", ", off)}");
        }

        output.WriteLine();
        output.WriteLine("Siguientes pasos:");
        output.WriteLine($"  cd {outputDir}");
        output.WriteLine($"  dotnet run --project src/{name}.Api");
        output.WriteLine($"  curl http://localhost:{ServiceName.DevPort(name)}/api/v1/ping");
    }
}
