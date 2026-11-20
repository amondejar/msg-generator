using Stratum.Core;
using Stratum.Core.Generation;
using Stratum.Core.Import;
using Stratum.Core.Model;
using Stratum.Core.Templating;
using Stratum.Studio;
using Stratum.Studio.State;

namespace Stratum.Cli;

/// <summary>
/// Hand-rolled argument parsing: Phase 1 has a single command and we want zero dependencies.
/// Move to System.CommandLine when the command set grows (add, regen, upgrade...).
/// </summary>
internal static partial class Cli
{
    private const string Usage = """
        Stratum {0} — generador de microservicios .NET

        Uso:
          stratum new <Nombre> [opciones]        Crea un servicio nuevo
          stratum new --from <stratum.json> [opc.]   Crea un servicio a partir de una definición
          stratum add entity <Nombre> --props "Campo:tipo, ..." [--plural <P>] [--db postgres|sqlserver] [--path] [--dry-run]
                                                 Añade una entidad con CRUD, EF Core y tests
          stratum entity list [--path <carpeta>]          Entidades del servicio y sus propiedades
          stratum entity update <Entidad> --props "..." [--rename Viejo=Nuevo]... [--path] [--dry-run]
                                                 Cambia las propiedades (las que falten se eliminan)
          stratum entity remove <Entidad> [--delete-own-files] [--path] [--dry-run]
                                                 Elimina una entidad (tus archivos solo con --delete-own-files)
          stratum entity operation add <Entidad> <Operación> [--params "..."] [--event <E>] [--path] [--dry-run]
                                                 Operación de negocio del agregado (clean-ddd): evento, comando,
                                                 POST /<entidades>/{id}/<operación>; la regla la escribes tú
          stratum entity operation remove <Entidad> <Operación> [--path] [--dry-run]
          stratum entity convert <Entidad> [--path] [--dry-run]
                                                 Pasa una entidad anterior a 0.10 al formato editable
          stratum feature list|on|off <feature>... [--path <carpeta>] [--dry-run]
                                                 Activa o desactiva features en un servicio existente
                                                 (observability, openApi, resilience, auth, tests, docker, ci)
          stratum feature convert [--path] [--dry-run]
                                                 Pasa un Program.cs anterior a 0.12 a StratumWiring.g.cs
          stratum messaging on --broker kafka|azureServiceBus|inMemory [--path] [--dry-run]
          stratum messaging off [--path] [--dry-run]
                                                 Eventos de integración entre servicios (outbox, inbox, reintentos)
          stratum events on|off <Entidad>... [--path] [--dry-run]
                                                 Publica <Entidad>Created/Updated/Deleted para otros servicios
          stratum subscribe <carpeta del publicador> <Entidad>[.Created|.Updated|.Deleted] [--path] [--dry-run]
                                                 Este servicio escucha eventos de otro (contrato + handler tuyo)
          stratum subscribe --sync [--prune] [--path] [--dry-run]
                                                 Vuelve a copiar los contratos si los publicadores cambiaron
                                                 (--prune quita las de eventos que ya no se publican)
          stratum unsubscribe <Publicador> <Entidad>[.<Evento>] [--path] [--dry-run]
          stratum import openapi <fichero|URL> [--name <S>] [-o <carpeta>] [--path <servicio>] [--dry-run]
                                                 Servicio (o entidades nuevas) desde una especificación OpenAPI
          stratum events map [--path <sistema>]          Quién publica y quién escucha cada evento
          stratum events asyncapi [--path] [-o <fichero>] AsyncAPI 3.1 de los eventos (asyncapi.yaml, o .json)
          stratum service remove <carpeta del servicio> [--path <sistema>] [--keep-files] [--yes]
                                                 Elimina un servicio y lo que cuelga de él (suscripciones de otros
                                                 servicios, AppHost, stratum.workspace.json). Sin --yes solo lo muestra
          stratum workspace list|add <carpeta>|remove <carpeta> [--path <sistema>]
                                                 Servicios de otras carpetas que forman parte del sistema
                                                 (stratum.workspace.json; los usan stratum aspire y stratum studio)
          stratum migration review [--path <carpeta>] [--dry-run]
                                                 Revisa la última migración de EF Core: aplica los renombrados
                                                 (RenameColumn) y avisa de lo que borra datos
          stratum aspire [--name <Sistema>] [--path <carpeta>]
                                                 AppHost de Aspire con todos los servicios de la carpeta
          stratum studio [--path <carpeta>] [--port 5150] [--no-browser]
                                                 Editor visual en el navegador
          stratum mcp [--path <sistema>] [--package-source <carpeta>]
                                                 Servidor MCP (stdio) para asistentes de IA: Claude, Copilot, Cursor
          stratum mcp config [--path <sistema>]          Configuración para Claude Desktop, Claude Code, VS Code y Cursor
          stratum templates list [--path <carpeta>]        Plantillas de Stratum y del equipo (.stratum/templates)
          stratum templates eject <set>[/<archivo>] [--path <carpeta>] [--force]
                                                 Copia plantillas a .stratum/templates para personalizarlas
          stratum upgrade [--path <servicio o sistema>] [--to <versión>] [--dry-run]
                                                 Actualiza los paquetes Stratum.* de un servicio existente
          stratum --version

        Opciones:
          -o, --output <carpeta>   Carpeta de salida (por defecto ./<Nombre>)
              --force              Sobrescribe si la carpeta no está vacía
              --dry-run            Muestra los archivos sin escribirlos
              --architecture <a>   vertical-slice (por defecto) o clean-ddd
              --package-source <carpeta>
                                   Usa paquetes Stratum.* empaquetados en local (antes de publicarlos
                                   en nuget.org). Genera un nuget.config con source mapping.

        Features (activas por defecto salvo auth y mcp):
          --[no-]observability     OpenTelemetry: trazas, métricas y logs
          --[no-]openapi           Documento OpenAPI + Scalar (/scalar)
          --[no-]resilience        Reintentos/timeout/circuit breaker en HttpClient
          --[no-]auth              JWT bearer, autenticado por defecto      (off)
          --[no-]mcp               Servidor MCP en /mcp con las entidades   (off)
          --[no-]tests             Tests de integración
          --[no-]docker            Dockerfile
          --[no-]ci                GitHub Actions

        Siempre incluido: estructura de solución, Central Package Management, health checks,
        ProblemDetails, correlation id, logs estructurados y configuración validada.
        Lo común llega como paquetes Stratum.ServiceDefaults.*: 'stratum upgrade' lo actualiza sin regenerar.

        Tipos de --props: string, int, long, decimal, bool, guid, date, datetime. Añade ? si es opcional.
        Referencia a otra entidad del servicio (clave ajena): "CustomerId:Customer" (o "CustomerId:Customer?").
          stratum add entity Product --props "Name:string, Price:decimal, Description:string?"
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        if (args.Length > 0 && args[0] == "studio")
        {
            return await Studio(args[1..], output).ConfigureAwait(false);
        }

        if (args.Length > 0 && args[0] == "mcp")
        {
            return await Mcp(args[1..]).ConfigureAwait(false);
        }

        return Run(args, output);
    }

    public static int Run(string[] args, TextWriter output)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine(Usage, StratumInfo.Version);
            return 0;
        }

        if (args[0] is "--version" or "-v")
        {
            output.WriteLine(StratumInfo.Version);
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
            "workspace" => WorkspaceCommand(args[1..], output),
            "service" => ServiceCommand(args[1..], output),
            "subscribe" => Subscribe(args[1..], output, subscribe: true),
            "unsubscribe" => Subscribe(args[1..], output, subscribe: false),
            "aspire" => Aspire(args[1..], output),
            "templates" => Templates(args[1..], output),
            "import" => Import(args[1..], output),
            _ => throw new StratumException($"Comando desconocido '{args[0]}'. Usa 'stratum --help'."),
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
                        throw new StratumException($"La carpeta de paquetes '{packageSource}' no existe. Créala con 'dotnet pack Stratum.slnx -c Release -o <carpeta>'.");
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
                    output.WriteLine(Usage, StratumInfo.Version);
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
                        throw new StratumException($"Argumento inesperado '{arg}'.");
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
                    Name = ServiceName.Normalize(name ?? throw new StratumException("Falta el nombre del servicio: stratum new <Nombre>")),
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
            throw new StratumException("Uso: stratum add entity <Nombre> --props \"Campo:tipo, ...\"");
        }

        string? name = null, props = null, plural = null, db = null;
        var path = ".";
        var dryRun = false;

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
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    if (name is null && !args[i].StartsWith('-'))
                    {
                        name = args[i];
                        break;
                    }

                    throw new StratumException($"Argumento inesperado '{args[i]}'.");
            }
        }

        var result = EntityAdder.Add(new AddEntityRequest
        {
            ServiceDirectory = path,
            Name = name ?? throw new StratumException("Falta el nombre de la entidad: stratum add entity <Nombre> --props \"...\""),
            Properties = props ?? throw new StratumException("Falta --props \"Campo:tipo, ...\" (p. ej. \"Name:string, Price:decimal\")."),
            Plural = plural,
            Provider = db,
            DryRun = dryRun,
        });

        var entity = result.Entity;
        if (dryRun)
        {
            output.WriteLine($"Vista previa (--dry-run, no se ha escrito nada): entidad {entity.Name} ({result.Provider}) → /api/v1/{ServiceName.ToKebab(entity.Plural)}");
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
                output.WriteLine($"⚠ {warning}");
            }

            return 0;
        }

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

    /// <summary>MCP server over stdio: stdout carries only protocol messages, diagnostics go to stderr.</summary>
    private static async Task<int> Mcp(string[] args)
    {
        if (args.Length > 0 && args[0] == "config")
        {
            return McpConfig(args[1..], Console.Out);
        }

        var root = ".";
        string? packageSource = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--path":
                    root = Value(args, ref i, args[i]);
                    break;
                case "--package-source":
                    packageSource = Path.GetFullPath(Value(args, ref i, args[i]));
                    break;
                default:
                    throw new StratumException($"Argumento inesperado '{args[i]}'. Uso: stratum mcp [--path <sistema>] [--package-source <carpeta>]");
            }
        }

        if (!Directory.Exists(root))
        {
            throw new StratumException($"No existe la carpeta '{Path.GetFullPath(root)}'.");
        }

        // Relative paths in tool calls (and the default folder of a new service) are relative to the system.
        Directory.SetCurrentDirectory(Path.GetFullPath(root));
        root = ".";

        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        await using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false };
        await Console.Error.WriteLineAsync($"stratum mcp {StratumInfo.Version}: sistema {Path.GetFullPath(root)}").ConfigureAwait(false);
        await new Mcp.McpServer(root, packageSource).RunAsync(input, output).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Configuration snippets for MCP clients, with this installation's paths.</summary>
    private static int McpConfig(string[] args, TextWriter output)
    {
        var root = ".";
        for (var i = 0; i < args.Length; i++)
        {
            root = args[i] == "--path" ? Value(args, ref i, args[i]) : throw new StratumException($"Argumento inesperado '{args[i]}'. Uso: stratum mcp config [--path <sistema>]");
        }

        root = Path.GetFullPath(root);

        // Installed as a dotnet tool the process is "stratum"; run from source it is "dotnet stratum.dll".
        var process = Environment.ProcessPath ?? "dotnet";
        var installed = !Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var command = installed ? process : "dotnet";
        string[] commandArgs = installed ? ["mcp", "--path", root] : [Path.Combine(AppContext.BaseDirectory, "stratum.dll"), "mcp", "--path", root];
        var json = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);

        output.WriteLine("Claude Code (en la carpeta del sistema):");
        output.WriteLine($"  claude mcp add stratum -- {(installed ? Quote(command) : "dotnet")} {string.Join(' ', commandArgs.Select(a => a.Contains(' ', StringComparison.Ordinal) ? Quote(a) : a))}");
        output.WriteLine();
        output.WriteLine("Claude Desktop (claude_desktop_config.json, dentro de \"mcpServers\"):");
        output.WriteLine("  \"stratum\": " + System.Text.Json.JsonSerializer.Serialize(new { command, args = commandArgs }, json).Replace("\n", "\n  ", StringComparison.Ordinal));
        output.WriteLine();
        output.WriteLine("VS Code / GitHub Copilot (.vscode/mcp.json, dentro de \"servers\"):");
        output.WriteLine("  \"stratum\": " + System.Text.Json.JsonSerializer.Serialize(new { type = "stdio", command, args = commandArgs }, json).Replace("\n", "\n  ", StringComparison.Ordinal));
        output.WriteLine();
        output.WriteLine("Cursor (.cursor/mcp.json, dentro de \"mcpServers\"): igual que Claude Desktop.");
        output.WriteLine();
        output.WriteLine("Las herramientas que cambian algo devuelven primero un plan; el asistente aplica con \"apply\": true cuando tú lo apruebas.");
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
                        : throw new StratumException("--port necesita un número entre 1 y 65535.");
                    break;
                case "--no-browser":
                    openBrowser = false;
                    break;
                case "--package-source":
                    packageSource = Path.GetFullPath(Value(args, ref i, args[i]));
                    break;
                default:
                    throw new StratumException($"Argumento inesperado '{args[i]}'. Uso: stratum studio [--path <carpeta>] [--port 5150] [--no-browser]");
            }
        }

        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
        {
            throw new StratumException($"No existe la carpeta '{full}'.");
        }

        // Opened on a service folder: the system is its parent, and that service is opened.
        var isService = ServiceGenerator.FindDefinition(full) is not null;

        // "dotnet run --project .../Stratum.Cli" starts in the project folder, not where you typed it:
        // never turn a .NET project folder into a system of services.
        if (!isService && Directory.EnumerateFiles(full, "*.csproj").Any())
        {
            throw new StratumException(
                $"'{full}' es un proyecto .NET, no una carpeta de servicios (¿lo lanzaste con dotnet run?). " +
                "Indica la carpeta con --path, p. ej.: stratum studio --path C:\\Repos\\servicios");
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
            throw new StratumException("Uso: stratum templates list | stratum templates eject <set>[/<archivo>]  (sets: " + string.Join(", ", TemplateSource.Sets) + ")");
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

                    throw new StratumException($"Argumento inesperado '{args[i]}'.");
            }
        }

        var team = TeamTemplates.Find(path);
        return args[0] == "list" ? ListTemplates(output, team) : EjectTemplates(output, team, path, target, force);
    }

    private static int ListTemplates(TextWriter output, TeamTemplates? team)
    {
        output.WriteLine(team is null
            ? "Sin plantillas del equipo (crea una con: stratum templates eject <set>/<archivo>)."
            : $"Plantillas del equipo: {team.Root}");
        if (team is not null && team.Tokens.Count > 0)
        {
            output.WriteLine($"  tokens: {string.Join(", ", team.Tokens.Keys.Select(k =>
                "{{" + k + "}}" + (ServiceGenerator.ReservedTokens.Contains(k) ? " (ignorado: es de Stratum)" : string.Empty)))}");
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
            throw new StratumException("Indica qué copiar: stratum templates eject service/src/__Service__.Api/Program.cs  (o un set entero: stratum templates eject service)");
        }

        var slash = target.IndexOf('/', StringComparison.Ordinal);
        var set = slash < 0 ? target : target[..slash];
        var file = slash < 0 ? null : target[(slash + 1)..];

        if (!TemplateSource.Sets.Contains(set))
        {
            throw new StratumException($"Set desconocido '{set}'. Sets: {string.Join(", ", TemplateSource.Sets)}.");
        }

        var selected = TemplateSource.LoadEmbedded(set).Where(f => file is null || f.Path == file).ToList();
        if (selected.Count == 0)
        {
            throw new StratumException($"No existe la plantilla '{target}'. Míralas con: stratum templates list");
        }

        var msgFolder = team?.Root ?? Path.Combine(Path.GetFullPath(path), TeamTemplates.FolderName);
        var config = Path.Combine(msgFolder, TeamTemplates.ConfigFile);
        if (!File.Exists(config))
        {
            Directory.CreateDirectory(msgFolder);
            File.WriteAllText(config, """
                {
                  // Tokens of your team, usable as {{Company}} in any template under .stratum/templates.
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
        output.WriteLine("Edítalas y haz commit de .stratum: stratum new, stratum add entity y stratum studio las usarán en todo el repo.");
        output.WriteLine("Un archivo vacío desactiva esa plantilla; un archivo nuevo se añade a lo que genera Stratum.");
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
                    throw new StratumException($"Argumento inesperado '{args[i]}'. Uso: stratum aspire [--name <Sistema>] [--path <carpeta>]");
            }
        }

        // A service folder is not a system: run it from the folder that contains the services.
        if (ServiceGenerator.FindDefinition(root) is not null)
        {
            throw new StratumException("Esta carpeta es un servicio. Ejecuta 'stratum aspire' en la carpeta que contiene tus servicios (o usa --path).");
        }

        var appHostName = ServiceName.Normalize(name ?? AspireGenerator.DefaultSystemName(root));
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
        var target = StratumInfo.Version;
        var dryRun = false;

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
                        throw new StratumException($"'{target}' no es una versión válida (p. ej. 0.3.0).");
                    }

                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    throw new StratumException($"Argumento inesperado '{args[i]}'. Uso: stratum upgrade [--path <servicio o sistema>] [--to <versión>] [--dry-run]");
            }
        }

        var full = Path.GetFullPath(path);
        var isSystem = ServiceGenerator.FindDefinition(full) is null;

        // 1. Services from before 0.22 (msg.json, Msg.* packages): move them to the Stratum names first.
        var legacy = LegacyMigration.LegacyServices(full);
        var edits = legacy.SelectMany(LegacyMigration.PlanService).ToList();
        if (isSystem)
        {
            edits.AddRange(LegacyMigration.PlanSystem(full));
        }

        if (edits.Count > 0)
        {
            output.WriteLine($"Nombres anteriores a Stratum 0.22 (Msg.*, msg.json, MsgWiring): {legacy.Count} servicio(s) a migrar{(dryRun ? " (--dry-run, no se escribe nada)" : string.Empty)}:");
            foreach (var edit in edits.OrderBy(e => e.Path, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(full, edit.Path).Replace('\\', '/');
                var mark = edit.NewPath is null ? "-" : edit.NewPath != edit.Path ? "→" : "~";
                var to = edit.NewPath is { } n && n != edit.Path ? $" → {Path.GetRelativePath(full, n).Replace('\\', '/')}" : string.Empty;
                output.WriteLine($"  {mark} {relative}{to}  ({edit.Reason})");
            }

            output.WriteLine("  Después se regeneran los .g.cs (StratumWiring.g.cs…) y el AppHost, y los paquetes pasan a Stratum.* " + target + ".");
            output.WriteLine("  Las tablas msg_outbox/msg_inbox y las cabeceras de los mensajes no cambian: la base de datos y los demás servicios siguen funcionando.");
            if (dryRun)
            {
                return 0;
            }

            LegacyMigration.Apply(edits);
            output.WriteLine();
        }
        else if (dryRun)
        {
            output.WriteLine("No hay nada con los nombres anteriores. Sin --dry-run, actualiza los paquetes Stratum.* a " + target + ".");
            return 0;
        }

        // 2. Package versions (and, for migrated services, the generated code with the new names).
        var services = isSystem
            ? AspireGenerator.Discover(full).Select(s => Path.GetFullPath(Path.Combine(full, s.RelativeDirectory))).ToList()
            : [full];
        if (services.Count == 0)
        {
            throw new StratumException($"No hay servicios de Stratum en '{full}'.");
        }

        foreach (var service in services)
        {
            UpgradeService(service, target, migrated: legacy.Contains(service), output);
        }

        if (isSystem && legacy.Count > 0 && Directory.EnumerateDirectories(full, "*.AppHost").Any())
        {
            var result = AspireGenerator.Generate(full, AspireGenerator.DefaultSystemName(full), AspireGenerator.Discover(full));
            output.WriteLine($"✔ {result.AppHostName} regenerado (StratumServices.g.cs)");
        }

        output.WriteLine();
        output.WriteLine("Siguientes pasos: dotnet build && dotnet test, y revisa las notas de la versión.");
        return 0;
    }

    private static void UpgradeService(string directory, string target, bool migrated, TextWriter output)
    {
        var result = ServiceUpgrader.Upgrade(directory, target);
        var name = Path.GetFileName(directory);
        if (migrated)
        {
            // Regenerate with the new templates: StratumWiring.g.cs and the rest of the .g.cs with the new names.
            var definition = ServicePlanner.Load(directory);
            var plan = ServicePlanner.Plan(directory, definition, definition, new GenerationOptions { Team = TeamTemplates.Find(directory) });
            plan.Apply();
            output.WriteLine($"✔ {name}: migrado a Stratum {result.ToVersion} ({plan.Changes.Count} archivo(s) generados de nuevo)");
            foreach (var warning in plan.Warnings)
            {
                output.WriteLine($"  ⚠ {warning}");
            }
        }
        else if (result.FromVersion == result.ToVersion)
        {
            output.WriteLine($"✔ {name}: ya está en Stratum {result.ToVersion}");
        }
        else
        {
            output.WriteLine($"✔ {name}: Stratum {result.FromVersion} → {result.ToVersion} ({string.Join(", ", result.Packages)})");
        }

        foreach (var hint in result.Hints)
        {
            output.WriteLine($"  ℹ {hint}");
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial System.Text.RegularExpressions.Regex SemVer();

    /// <summary>"stratum import openapi": a new service (or new entities in an existing one) from an OpenAPI document.</summary>
    private static int Import(string[] args, TextWriter output)
    {
        const string usage = "Uso: stratum import openapi <fichero o URL .json|.yaml> [--name <Servicio>] [-o <carpeta>] [--architecture vertical-slice|clean-ddd]\n" +
                             "     [--db postgres|sqlserver] [--path <servicio existente>] [--dry-run] [opciones de 'stratum new']";
        if (args.Length < 2 || args[0] != "openapi")
        {
            throw new StratumException(usage);
        }

        var source = args[1];
        string? name = null, existing = null, architecture = null, db = null;
        var dryRun = false;
        var passThrough = new List<string>();
        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--name":
                    name = Value(args, ref i, args[i]);
                    break;
                case "--path":
                    existing = Value(args, ref i, args[i]);
                    break;
                case "--architecture" or "-a":
                    architecture = Architectures.Validate(Value(args, ref i, args[i]).ToLowerInvariant());
                    break;
                case "--db":
                    db = Value(args, ref i, args[i]).ToLowerInvariant();
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "-o" or "--output" or "--package-source":
                    passThrough.Add(args[i]);
                    passThrough.Add(Value(args, ref i, args[i]));
                    break;
                default:
                    if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new StratumException($"Argumento inesperado '{args[i]}'. {usage}");
                    }

                    passThrough.Add(args[i]);   // --no-docker, --auth, --force...
                    break;
            }
        }

        var text = source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? Download(source)
            : File.Exists(source) ? File.ReadAllText(source) : throw new StratumException($"No existe el archivo '{source}'.");
        var doc = OpenApiImporter.Load(text);

        ServiceDefinition? current = existing is not null ? ServicePlanner.Load(existing) : null;
        var prepared = OpenApiImporter.Prepare(doc, current, name, architecture);
        var accepted = prepared.Entities;
        var serviceName = prepared.ServiceName;
        var definition = current ?? new ServiceDefinition { Service = new ServiceInfo { Name = serviceName }, Architecture = prepared.Architecture };
        output.WriteLine(prepared.Report());
        output.WriteLine();
        if (accepted.Count == 0)
        {
            output.WriteLine("No hay nada que generar.");
            return 0;
        }

        if (current is not null)
        {
            var provider = ServicePlanner.ResolveProvider(db, current.Persistence);
            var plan = ServicePlanner.AddEntities(existing!, current, accepted, provider, new GenerationOptions { Team = TeamTemplates.Find(existing!) });
            PrintPlan(output, plan, dryRun);
            if (!dryRun && plan.Changes.Count > 0)
            {
                plan.Apply();
            }

            return 0;
        }

        var full = definition with
        {
            Entities = accepted,
            Persistence = new PersistenceSettings { Provider = ServicePlanner.ResolveProvider(db, null) },
            StratumVersion = StratumInfo.Version,
        };
        ModelRules.Validate(full);
        if (dryRun)
        {
            output.WriteLine($"Vista previa (--dry-run): se crearía el servicio {serviceName}. Repite sin --dry-run para crearlo.");
            return 0;
        }

        var temp = Path.Combine(Path.GetTempPath(), $"stratum-import-{Guid.NewGuid():N}.json");
        File.WriteAllText(temp, ServiceGenerator.Serialize(full));
        try
        {
            return New(["--from", temp, .. passThrough], output);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static string Download(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            return http.GetStringAsync(new Uri(url)).GetAwaiter().GetResult();
        }
        catch (HttpRequestException ex)
        {
            throw new StratumException($"No se pudo descargar {url}: {ex.Message}", ex);
        }
    }

    private static ServiceDefinition LoadDefinition(string path, string? nameOverride)
    {
        if (!File.Exists(path))
        {
            throw new StratumException($"No existe el archivo '{path}'.");
        }

        var definition = ServiceGenerator.Deserialize(File.ReadAllText(path));
        return nameOverride is null
            ? definition
            : definition with { Service = definition.Service with { Name = ServiceName.Normalize(nameOverride) } };
    }

    private static int Entity(string[] args, TextWriter output)
    {
        const string usage = "Uso: stratum entity list|update|remove|convert <Entidad> [opciones]. Usa 'stratum --help'.";
        if (args.Length == 0)
        {
            throw new StratumException(usage);
        }

        if (args[0] == "operation")
        {
            return EntityOperations(args[1..], output);
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
                        throw new StratumException("--rename espera Viejo=Nuevo (p. ej. --rename Name=Title).");
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

                    throw new StratumException($"Argumento inesperado '{args[i]}'.");
            }
        }

        var current = ServicePlanner.Load(path);
        if (action == "list")
        {
            ListEntities(output, current);
            return 0;
        }

        var entityName = name ?? throw new StratumException($"Falta el nombre de la entidad. {usage}");
        var updated = action switch
        {
            "update" => ServicePlanner.UpdateEntity(
                current,
                entityName,
                PropertyTypes.Parse(props ?? throw new StratumException("Falta --props con la lista completa de propiedades (las que no estén se eliminan).")),
                renames),
            "remove" => ServicePlanner.RemoveEntity(current, entityName),
            "convert" => ServicePlanner.ConvertEntity(current, entityName),
            _ => throw new StratumException(usage),
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
                output.WriteLine($"  stratum migration review{(path == "." ? string.Empty : $" --path {path}")}");
            }
        }

        return 0;
    }

    /// <summary>"stratum entity operation add|remove": business operations of an aggregate (clean-ddd).</summary>
    private static int EntityOperations(string[] args, TextWriter output)
    {
        const string usage = "Uso: stratum entity operation add <Entidad> <Operación> [--params \"Campo:tipo, ...\"] [--event <Evento>] [--path] [--dry-run]\n" +
                             "     stratum entity operation remove <Entidad> <Operación> [--path] [--dry-run]";
        if (args.Length == 0 || args[0] is not ("add" or "remove"))
        {
            throw new StratumException(usage);
        }

        var action = args[0];
        var positional = new List<string>();
        string? parameters = null, eventName = null;
        var path = ".";
        var dryRun = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--params" when action == "add":
                    parameters = Value(args, ref i, args[i]);
                    break;
                case "--event" when action == "add":
                    eventName = Value(args, ref i, args[i]);
                    break;
                case "--path":
                    path = Value(args, ref i, args[i]);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    if (!args[i].StartsWith('-') && positional.Count < 2)
                    {
                        positional.Add(args[i]);
                        break;
                    }

                    throw new StratumException($"Argumento inesperado '{args[i]}'. {usage}");
            }
        }

        if (positional.Count < 2)
        {
            throw new StratumException(usage);
        }

        var current = ServicePlanner.Load(path);
        var entity = ServicePlanner.FindEntity(current, positional[0]);
        var updated = action == "add"
            ? ServicePlanner.AddOperation(current, entity.Name, ModelRules.ParseOperation(entity.Name, positional[1], parameters, eventName))
            : ServicePlanner.RemoveOperation(current, entity.Name, positional[1]);

        var plan = ServicePlanner.Plan(path, current, updated, new GenerationOptions { Team = TeamTemplates.Find(path) });
        PrintPlan(output, plan, dryRun);
        if (!dryRun && plan.Changes.Count > 0)
        {
            plan.Apply();
            if (action == "add")
            {
                var operation = ServiceName.Normalize(positional[1]);
                output.WriteLine();
                output.WriteLine($"Escribe la regla en src/{current.Service.Name}.Domain/{entity.Plural}/{entity.Name}.{operation}.cs (es tuyo).");
                output.WriteLine($"Endpoint: POST /api/v1/{EntityCodeGen.Kebab(entity.Plural)}/{{id}}/{EntityCodeGen.Kebab(operation)}");
            }
            else
            {
                output.WriteLine();
                output.WriteLine($"Tu archivo {entity.Name}.{ServiceName.Normalize(positional[1])}.cs se conserva: bórralo si ya no lo necesitas.");
            }
        }

        return 0;
    }

    private static int Feature(string[] args, TextWriter output)
    {
        const string usage = "Uso: stratum feature list|on|off <feature>... | stratum feature convert  [--path <carpeta>] [--dry-run]";
        if (args.Length == 0 || args[0] is not ("list" or "on" or "off" or "convert"))
        {
            throw new StratumException(usage);
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
                        throw new StratumException($"Argumento inesperado '{args[i]}'. {usage}");
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

            if (!ServicePlanner.UsesStratumWiring(path, current))
            {
                output.WriteLine();
                output.WriteLine("Program.cs es anterior a Stratum 0.12: para cambiar features, primero: stratum feature convert --dry-run");
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
                throw new StratumException($"Indica qué features. Válidas: {string.Join(", ", FeatureSet.Names)}.");
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
        const string usage = "Uso: stratum messaging on --broker kafka|azureServiceBus|inMemory | stratum messaging off  [--path <carpeta>] [--dry-run]";
        if (args.Length == 0 || args[0] is not ("on" or "off"))
        {
            throw new StratumException(usage);
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
                    throw new StratumException($"Argumento inesperado '{args[i]}'. {usage}");
            }
        }

        if (args[0] == "on" && broker is null)
        {
            throw new StratumException($"Falta --broker. Válidos: {string.Join(", ", MessagingSettings.Brokers)}.");
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
                output.WriteLine("Para ejecutarlo con Kafka, regenera el AppHost (stratum aspire, o desde el Studio): añade Kafka y Kafka UI.");
            }
            else if (updated.Messaging?.Broker == MessagingSettings.AzureServiceBus)
            {
                output.WriteLine("Para ejecutarlo, regenera el AppHost (stratum aspire, o desde el Studio): añade el emulador de Service Bus con sus topics y suscripciones.");
            }
        }

        return 0;
    }

    private static int Events(string[] args, TextWriter output)
    {
        if (args.Length > 0 && args[0] == "map")
        {
            return EventsMap(args[1..], output);
        }

        if (args.Length > 0 && args[0] == "asyncapi")
        {
            return EventsAsyncApi(args[1..], output);
        }

        const string usage = "Uso: stratum events on|off <Entidad>... [--path <carpeta>] [--dry-run]";
        if (args.Length < 2 || args[0] is not ("on" or "off"))
        {
            throw new StratumException(usage);
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
                        throw new StratumException($"Argumento inesperado '{args[i]}'. {usage}");
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

    private static int Subscribe(string[] args, TextWriter output, bool subscribe)
    {
        var usage = subscribe
            ? "Uso: stratum subscribe <carpeta del publicador> <Entidad>[.Evento] | stratum subscribe --sync  [--path <servicio>] [--dry-run]"
            : "Uso: stratum unsubscribe <Publicador> <Entidad>[.Evento] [--path <servicio>] [--dry-run]";
        var path = ".";
        bool dryRun = false, sync = false, prune = false;
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--path":
                    path = Value(args, ref i, args[i]);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--sync" when subscribe:
                    sync = true;
                    break;
                case "--prune" when subscribe:
                    prune = true;
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        throw new StratumException($"Argumento inesperado '{args[i]}'. {usage}");
                    }

                    positional.Add(args[i]);
                    break;
            }
        }

        var current = ServicePlanner.Load(path);
        ServiceDefinition updated;
        if (sync)
        {
            var others = Neighbours(path);
            updated = ServicePlanner.SyncSubscriptions(current, others, prune);
            foreach (var (subscription, latest, problem) in ServicePlanner.CheckSubscriptions(current, others).Where(c => c.Current is null))
            {
                output.WriteLine(prune ? $"✔ {subscription.Topic}: se quita ({problem})" : $"⚠ {subscription.Topic}: {problem} (--prune la quita)");
            }
        }
        else
        {
            if (positional.Count != 2)
            {
                throw new StratumException(usage);
            }

            var parts = positional[1].Split('.', 2);
            var events = parts.Length == 2 ? [parts[1]] : Subscription.Events;
            updated = current;
            if (subscribe)
            {
                var publisher = ServicePlanner.Load(positional[0]);
                foreach (var name in events)
                {
                    updated = ServicePlanner.Subscribe(updated, ServicePlanner.SubscriptionTo(publisher, parts[0], name));
                }

                if (publisher.Messaging?.Broker != current.Messaging?.Broker)
                {
                    output.WriteLine($"⚠ {publisher.Service.Name} usa '{publisher.Messaging?.Broker}' y este servicio '{current.Messaging?.Broker}': " +
                                     "para que los eventos lleguen, los dos deben usar el mismo broker (Kafka).");
                }
            }
            else
            {
                foreach (var name in events)
                {
                    updated = ServicePlanner.Unsubscribe(updated, positional[0], parts[0], name);
                }
            }
        }

        var plan = ServicePlanner.Plan(path, current, updated, new GenerationOptions { Team = TeamTemplates.Find(path) });
        PrintPlan(output, plan, dryRun);
        if (!dryRun && plan.Changes.Count > 0)
        {
            plan.Apply();
        }

        return 0;
    }

    /// <summary>The other services of the system the service belongs to (its parent folder, plus stratum.workspace.json).</summary>
    private static List<ServiceDefinition> Neighbours(string serviceDirectory)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(serviceDirectory)) ?? serviceDirectory;
        return [.. AspireGenerator.Discover(parent).Select(s => s.Definition)];
    }

    private static int EventsMap(string[] args, TextWriter output)
    {
        var root = ".";
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--path")
            {
                root = Value(args, ref i, args[i]);
            }
            else
            {
                throw new StratumException($"Argumento inesperado '{args[i]}'. Uso: stratum events map [--path <sistema>]");
            }
        }

        var routes = EventMap.Build(AspireGenerator.Discover(root).Select(s => s.Definition));
        if (routes.Count == 0)
        {
            output.WriteLine("Ningún servicio publica ni escucha eventos todavía (stratum events on <Entidad>, stratum subscribe ...).");
            return 0;
        }

        foreach (var route in routes)
        {
            var consumers = route.Consumers.Count == 0 ? "(nadie lo escucha)" : string.Join(", ", route.Consumers);
            output.WriteLine($"  {route.Topic,-40} {route.Publisher ?? "¿?",-14} → {consumers}");
            if (route.Problem is { } problem)
            {
                output.WriteLine($"    ⚠ {problem}");
            }
        }

        return 0;
    }

    private static int EventsAsyncApi(string[] args, TextWriter output)
    {
        const string usage = "Uso: stratum events asyncapi [--path <sistema>] [-o <fichero .yaml|.json>] [--title <título>]";
        var root = ".";
        string? file = null;
        string? title = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--path":
                    root = Value(args, ref i, args[i]);
                    break;
                case "-o" or "--output":
                    file = Value(args, ref i, args[i]);
                    break;
                case "--title":
                    title = Value(args, ref i, args[i]);
                    break;
                default:
                    throw new StratumException($"Argumento inesperado '{args[i]}'. {usage}");
            }
        }

        var services = AspireGenerator.Discover(root).Select(s => s.Definition).ToList();
        if (services.Count == 0)
        {
            throw new StratumException($"No hay servicios (stratum.json) en '{Path.GetFullPath(root)}'.");
        }

        var document = AsyncApiDocument.Build(services, title ?? $"{new DirectoryInfo(Path.GetFullPath(root)).Name} events");
        var path = Path.GetFullPath(file ?? Path.Combine(root, "asyncapi.yaml"));
        var json = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(path, json ? AsyncApiDocument.ToJson(document) : AsyncApiDocument.ToYaml(document));
        var channels = document["channels"]!.AsObject().Count;
        output.WriteLine($"AsyncAPI {AsyncApiDocument.Version}: {channels} evento(s) → {path}");
        if (channels == 0)
        {
            output.WriteLine("Todavía no hay eventos: stratum events on <Entidad> en un servicio, stratum subscribe en otro.");
        }

        return 0;
    }

    private static int ServiceCommand(string[] args, TextWriter output)
    {
        const string usage = "Uso: stratum service remove <carpeta del servicio> [--path <sistema>] [--keep-files] [--yes]";
        if (args.Length < 2 || args[0] != "remove")
        {
            throw new StratumException(usage);
        }

        string? root = null;
        bool keepFiles = false, yes = false;
        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--path":
                    root = Value(args, ref i, args[i]);
                    break;
                case "--keep-files":
                    keepFiles = true;
                    break;
                case "--yes":
                    yes = true;
                    break;
                default:
                    throw new StratumException($"Argumento inesperado '{args[i]}'. {usage}");
            }
        }

        var serviceDirectory = Path.GetFullPath(args[1]);
        // The system: --path, or the folder that contains the service.
        root ??= Path.GetDirectoryName(serviceDirectory) ?? ".";
        var plan = ServiceRemoval.Plan(root, serviceDirectory);

        output.WriteLine($"Eliminar {plan.ServiceName} ({plan.Directory}):");
        output.WriteLine(keepFiles
            ? "  · La carpeta y sus archivos se conservan (solo sale del sistema)."
            : $"  - Se borra la carpeta con sus {plan.FileCount} archivo(s).");
        foreach (var consumer in plan.Consumers)
        {
            output.WriteLine($"  - {consumer.Service} deja de escuchar {string.Join(", ", consumer.Subscriptions.Select(s => s.Topic))}:");
            foreach (var change in consumer.Plan.Changes)
            {
                output.WriteLine($"      {(change.Kind == ChangeKind.Delete ? "-" : "~")} {change.Path}");
            }

            foreach (var warning in consumer.Plan.Warnings)
            {
                output.WriteLine($"      ⚠ {warning.Split('\n')[0]}");
            }
        }

        if (plan.AppHost is { } appHost)
        {
            output.WriteLine($"  ~ {appHost}: se regenera sin el servicio.");
        }

        foreach (var note in plan.Notes)
        {
            output.WriteLine($"  ⚠ {note}");
        }

        if (!yes)
        {
            output.WriteLine();
            output.WriteLine("No se ha tocado nada. Para eliminarlo de verdad, repite el comando con --yes.");
            return 0;
        }

        foreach (var line in ServiceRemoval.Apply(plan, deleteFolder: !keepFiles))
        {
            output.WriteLine($"✔ {line}");
        }

        return 0;
    }

    private static int WorkspaceCommand(string[] args, TextWriter output)
    {
        const string usage = "Uso: stratum workspace list | stratum workspace add <carpeta> | stratum workspace remove <carpeta>  [--path <sistema>]";
        if (args.Length == 0 || args[0] is not ("list" or "add" or "remove"))
        {
            throw new StratumException(usage);
        }

        var root = ".";
        string? target = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--path")
            {
                root = Value(args, ref i, args[i]);
            }
            else if (target is null && args[0] != "list" && !args[i].StartsWith('-'))
            {
                target = args[i];
            }
            else
            {
                throw new StratumException($"Argumento inesperado '{args[i]}'. {usage}");
            }
        }

        if (args[0] == "list")
        {
            var services = AspireGenerator.Discover(root);
            if (services.Count == 0)
            {
                output.WriteLine("No hay servicios de Stratum en el sistema.");
            }

            foreach (var service in services)
            {
                var full = Path.GetFullPath(Path.Combine(root, service.RelativeDirectory));
                output.WriteLine($"  {service.Name,-20} {full}{(WorkspaceFile.IsInside(root, full) ? string.Empty : "   (stratum.workspace.json)")}");
            }

            return 0;
        }

        var path = target ?? throw new StratumException($"Falta la carpeta del servicio. {usage}");
        if (args[0] == "add")
        {
            output.WriteLine(WorkspaceFile.Add(root, path)
                ? $"✔ {Path.GetFullPath(path)} forma parte del sistema (stratum.workspace.json). Regenera el AppHost con stratum aspire."
                : "Nada que añadir: ya está en el sistema (o dentro de su carpeta, donde Stratum lo encuentra solo).");
        }
        else
        {
            output.WriteLine(WorkspaceFile.Remove(root, path)
                ? $"✔ {path} ya no forma parte del sistema (sus archivos siguen donde estaban). Regenera el AppHost con stratum aspire."
                : "No estaba en stratum.workspace.json.");
        }

        return 0;
    }

    private static int Migration(string[] args, TextWriter output)
    {
        if (args.Length == 0 || args[0] != "review")
        {
            throw new StratumException("Uso: stratum migration review [--path <carpeta>] [--dry-run]");
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
                    throw new StratumException($"Argumento inesperado '{args[i]}'.");
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
                : "  La migración se ha corregido; los renombrados ya no están pendientes en stratum.json.");
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
            output.WriteLine("El servicio no tiene entidades. Añade una con: stratum add entity <Nombre> --props \"...\"");
            return;
        }

        foreach (var entity in definition.Entities)
        {
            var props = PropertyTypes.Spec(entity.Properties);
            output.WriteLine($"{entity.Name} ({entity.Plural}){(entity.IsEditable ? string.Empty : "  [anterior a 0.10: stratum entity convert " + entity.Name + "]")}");
            output.WriteLine($"  --props \"{props}\"");
            foreach (var operation in entity.Operations ?? [])
            {
                output.WriteLine($"  operación {operation.Name}({PropertyTypes.Spec(operation.Parameters)}) → {operation.Event}");
            }

            foreach (var (dependent, property) in ModelRules.Dependents(definition, entity.Name))
            {
                output.WriteLine($"  la usa {dependent.Name}.{property.Name}");
            }
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
            output.WriteLine("Nada que cambiar: el código ya coincide con stratum.json.");
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
        ?? throw new StratumException($"Opción desconocida '{arg}'. Usa 'stratum --help'.");

    private static string Value(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
        {
            throw new StratumException($"La opción {option} necesita un valor.");
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
