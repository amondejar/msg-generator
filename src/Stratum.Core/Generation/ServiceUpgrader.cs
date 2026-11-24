using System.Text.RegularExpressions;

namespace Stratum.Core.Generation;

/// <param name="Regenerated">Generated files (.g.cs…) rewritten with the new version's templates.</param>
public sealed record UpgradeResult(string FromVersion, string ToVersion, IReadOnlyList<string> Packages, IReadOnlyList<string> Hints, int Regenerated = 0);

/// <summary>
/// Upgrades an existing service to another Stratum version without touching its code:
/// bumps every Stratum.* package in Directory.Packages.props and stratumVersion in stratum.json.
/// </summary>
public static partial class ServiceUpgrader
{
    public const string PackagesFileName = "Directory.Packages.props";

    [GeneratedRegex("""(?<prefix><PackageVersion\s+Include="(?<id>Stratum\.[^"]+)"\s+Version=")(?<version>[^"]+)(?<suffix>")""")]
    private static partial Regex StratumPackageVersion();

    /// <summary>The versions of the Stratum.* packages a service uses (empty if it has none).</summary>
    public static IReadOnlyList<string> PackageVersions(string serviceDirectory)
    {
        var packagesPath = Path.Combine(serviceDirectory, PackagesFileName);
        try
        {
            return File.Exists(packagesPath)
                ? [.. StratumPackageVersion().Matches(File.ReadAllText(packagesPath)).Select(m => m.Groups["version"].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]
                : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>True when some Stratum.* package of the service isn't at <paramref name="version"/>.</summary>
    public static bool IsBehind(string serviceDirectory, string version) =>
        PackageVersions(serviceDirectory) is { Count: > 0 } versions && versions.Any(v => v != version);

    public static UpgradeResult Upgrade(string serviceDirectory, string targetVersion)
    {
        ArgumentNullException.ThrowIfNull(serviceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);

        var definitionPath = Path.Combine(serviceDirectory, ServiceGenerator.DefinitionFileName);
        var packagesPath = Path.Combine(serviceDirectory, PackagesFileName);

        if (!File.Exists(definitionPath))
        {
            throw new StratumException($"No hay {ServiceGenerator.DefinitionFileName} en '{Path.GetFullPath(serviceDirectory)}'. ¿Es un servicio generado con Stratum?");
        }

        var definition = ServiceGenerator.Deserialize(File.ReadAllText(definitionPath));
        var props = File.Exists(packagesPath) ? File.ReadAllText(packagesPath) : string.Empty;

        var packages = StratumPackageVersion().Matches(props).Select(m => m.Groups["id"].Value).ToList();
        if (packages.Count == 0)
        {
            throw new StratumException(
                $"Este servicio (Stratum {definition.StratumVersion}) no usa los paquetes Stratum.ServiceDefaults: se generó antes de la 0.2.0 " +
                "con el código común copiado dentro. Regenera la base con 'stratum new --from stratum.json -o <carpeta-nueva>' " +
                "y mueve tus endpoints al nuevo proyecto.");
        }

        var updated = StratumPackageVersion().Replace(props, m => m.Groups["prefix"].Value + targetVersion + m.Groups["suffix"].Value);
        File.WriteAllText(packagesPath, updated);
        File.WriteAllText(definitionPath, ServiceGenerator.Serialize(definition with { StratumVersion = targetVersion }));

        // The new packages may come with new generated code (e.g. the msg_received table in 0.35): Stratum's own
        // files are rewritten with this version's templates; the user's files aren't touched.
        var hints = Hints(serviceDirectory, definition);
        var regenerated = 0;

        // Where to restore the new Stratum.* packages from (they come with the tool until they're on nuget.org).
        var nugetConfig = Path.Combine(serviceDirectory, "nuget.config");
        if (StratumPackages.LocalSource() is { } source)
        {
            if (StratumPackages.EnsureSource(nugetConfig, source))
            {
                hints.Add($"nuget.config: los paquetes Stratum.* se restauran de {source} (vienen con la herramienta stratum).");
            }
        }
        else if (StratumInfo.IsPublished && StratumPackages.RemoveLocalOnlyConfig(nugetConfig))
        {
            hints.Add("nuget.config eliminado: solo servía para los paquetes Stratum.* locales y ya están en nuget.org.");
        }

        try
        {
            var current = ServicePlanner.Load(serviceDirectory);
            if (targetVersion == StratumInfo.Version)   // the templates are this tool's
            {
                var plan = ServicePlanner.Plan(serviceDirectory, current, current, new GenerationOptions { Team = Templating.TeamTemplates.Find(serviceDirectory) });
                if (plan.Changes.Where(c => c.HandEdited).Select(c => c.Path).ToList() is { Count: > 0 } edited)
                {
                    hints.Add($"El código generado no se ha regenerado porque hay .g.cs editados a mano ({string.Join(", ", edited)}). " +
                              "Revísalos y aplica cualquier cambio en el Studio (o stratum entity …) para regenerarlos.");
                }
                else
                {
                    plan.Apply();
                    regenerated = plan.Changes.Count(c => c.Path != ServiceGenerator.DefinitionFileName);
                }
            }
        }
        catch (StratumException e)
        {
            hints.Add($"No se ha regenerado el código generado con las plantillas de {targetVersion}: {e.Message}");
        }

        return new UpgradeResult(definition.StratumVersion, targetVersion, packages, hints, regenerated);
    }

    /// <summary>
    /// Improvements of newer templates that live in user-owned files (Program.cs, .csproj): Stratum can't apply
    /// them without overwriting your code, so it tells you exactly what to add.
    /// </summary>
    private static List<string> Hints(string directory, Stratum.Core.Model.ServiceDefinition definition)
    {
        var hints = new List<string>();
        var service = definition.Service.Name;
        var program = Path.Combine(directory, "src", $"{service}.Api", "Program.cs");

        var usesWiring = File.Exists(program) && File.ReadAllText(program).Contains("AddStratumWiring()", StringComparison.Ordinal);
        if (!usesWiring && File.Exists(program))
        {
            hints.Add(
                "Desde Stratum 0.12 las features (OpenAPI, auth, telemetría…) se configuran en StratumWiring.g.cs y se pueden activar o " +
                "desactivar después de crear el servicio. Para pasar tu Program.cs a ese formato: stratum feature convert --dry-run");
        }

        if (!usesWiring && definition.Features.OpenApi && File.Exists(program)
            && !File.ReadAllText(program).Contains("MapScalarApiReference", StringComparison.Ordinal))
        {
            hints.Add(
                "Scalar (explorar y probar la API en /scalar) llegó en Stratum 0.7.0. Para añadirlo:\n" +
                "      Directory.Packages.props:  <PackageVersion Include=\"Scalar.AspNetCore\" Version=\"2.21.0\" />\n" +
                $"      src/{service}.Api/{service}.Api.csproj:  <PackageReference Include=\"Scalar.AspNetCore\" />\n" +
                "      Program.cs:  using Scalar.AspNetCore;   y, junto a app.MapOpenApi():  app.MapScalarApiReference();");
        }

        var editorConfig = Path.Combine(directory, ".editorconfig");
        if (File.Exists(editorConfig) && !File.ReadAllText(editorConfig).Contains("Migrations", StringComparison.Ordinal))
        {
            hints.Add(
                "Las migraciones de EF Core (dotnet ef) no siguen las reglas de estilo del repo y rompen el build. Añade al final de .editorconfig:\n" +
                "      [**/Migrations/*.cs]\n" +
                "      generated_code = true");
        }

        if (definition.Messaging is not null && definition.Persistence is not null && !HasMigrationFor(directory, "msg_received"))
        {
            hints.Add(
                "Stratum 0.35 añade la tabla msg_received (lo que recibe cada handler, también los fallos: las trazas de eventos del Studio). " +
                "Crea su migración: en el Studio, Datos y migraciones; o dotnet ef migrations add StratumMessagingJournal. Sin ella los eventos se " +
                "siguen procesando, pero las trazas no muestran lo que recibe este servicio.");
        }

        var legacy = definition.Entities.Where(e => !e.IsEditable).Select(e => e.Name).ToList();
        if (legacy.Count > 0)
        {
            hints.Add(
                $"Entidades creadas antes de Stratum 0.10 ({string.Join(", ", legacy)}): Stratum no puede editarlas. Para cambiar sus " +
                "propiedades desde el Studio o con 'stratum entity update', conviértelas antes: stratum entity convert <Entidad> --dry-run");
        }

        return hints;
    }

    /// <summary>Some migration of the service already mentions <paramref name="table"/>.</summary>
    private static bool HasMigrationFor(string directory, string table)
    {
        var src = Path.Combine(directory, "src");
        return Directory.Exists(src) && Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Any(f => File.ReadAllText(f).Contains($"\"{table}\"", StringComparison.Ordinal));
    }
}
