using System.Text.RegularExpressions;

namespace Stratum.Core.Generation;

public sealed record UpgradeResult(string FromVersion, string ToVersion, IReadOnlyList<string> Packages, IReadOnlyList<string> Hints);

/// <summary>
/// Upgrades an existing service to another Stratum version without touching its code:
/// bumps every Stratum.* package in Directory.Packages.props and stratumVersion in stratum.json.
/// </summary>
public static partial class ServiceUpgrader
{
    public const string PackagesFileName = "Directory.Packages.props";

    [GeneratedRegex("""(?<prefix><PackageVersion\s+Include="(?<id>Stratum\.[^"]+)"\s+Version=")(?<version>[^"]+)(?<suffix>")""")]
    private static partial Regex StratumPackageVersion();

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

        return new UpgradeResult(definition.StratumVersion, targetVersion, packages, Hints(serviceDirectory, definition));
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

        var legacy = definition.Entities.Where(e => !e.IsEditable).Select(e => e.Name).ToList();
        if (legacy.Count > 0)
        {
            hints.Add(
                $"Entidades creadas antes de Stratum 0.10 ({string.Join(", ", legacy)}): Stratum no puede editarlas. Para cambiar sus " +
                "propiedades desde el Studio o con 'stratum entity update', conviértelas antes: stratum entity convert <Entidad> --dry-run");
        }

        return hints;
    }
}
