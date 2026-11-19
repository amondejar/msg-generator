using Msg.Core;
using Msg.Core.Generation;
using Msg.Core.Model;

namespace Msg.Cli;

/// <summary>
/// Hand-rolled argument parsing: Phase 1 has a single command and we want zero dependencies.
/// Move to System.CommandLine when the command set grows (add, regen, upgrade...).
/// </summary>
internal static class Cli
{
    private const string Usage = """
        MSG {0} — generador de microservicios .NET

        Uso:
          msg new <Nombre> [opciones]        Crea un servicio nuevo
          msg new --from <msg.json> [opc.]   Crea un servicio a partir de una definición
          msg --version

        Opciones:
          -o, --output <carpeta>   Carpeta de salida (por defecto ./<Nombre>)
              --force              Sobrescribe si la carpeta no está vacía
              --dry-run            Muestra los archivos sin escribirlos

        Features (activas por defecto salvo auth):
          --[no-]observability     OpenTelemetry: trazas, métricas y logs
          --[no-]openapi           Documento OpenAPI
          --[no-]resilience        Reintentos/timeout/circuit breaker en HttpClient
          --[no-]auth              JWT bearer, autenticado por defecto      (off)
          --[no-]tests             Tests de integración
          --[no-]docker            Dockerfile
          --[no-]ci                GitHub Actions

        Siempre incluido: estructura de solución, Central Package Management, health checks,
        ProblemDetails, correlation id, logs estructurados y configuración validada.
        """;

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
            _ => throw new MsgException($"Comando desconocido '{args[0]}'. Usa 'msg --help'."),
        };
    }

    private static int New(string[] args, TextWriter output)
    {
        string? name = null, outputDir = null, from = null;
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
                case "--force":
                    force = true;
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

        definition = definition with { Features = features };

        var files = ServiceGenerator.Generate(definition);
        var serviceName = definition.Service.Name;
        outputDir ??= serviceName;

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
        return 0;
    }

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
        var on = FeatureSet.Names.Where(features.IsEnabled);
        var off = FeatureSet.Names.Where(f => !features.IsEnabled(f)).ToList();

        output.WriteLine($"✔ Servicio {name} creado en {Path.GetFullPath(outputDir)} ({fileCount} archivos)");
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
