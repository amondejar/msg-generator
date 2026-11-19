using System.Text.RegularExpressions;

namespace Msg.Core.Generation;

public sealed record UpgradeResult(string FromVersion, string ToVersion, IReadOnlyList<string> Packages);

/// <summary>
/// Upgrades an existing service to another MSG version without touching its code:
/// bumps every Msg.* package in Directory.Packages.props and msgVersion in msg.json.
/// </summary>
public static partial class ServiceUpgrader
{
    public const string PackagesFileName = "Directory.Packages.props";

    [GeneratedRegex("""(?<prefix><PackageVersion\s+Include="(?<id>Msg\.[^"]+)"\s+Version=")(?<version>[^"]+)(?<suffix>")""")]
    private static partial Regex MsgPackageVersion();

    public static UpgradeResult Upgrade(string serviceDirectory, string targetVersion)
    {
        ArgumentNullException.ThrowIfNull(serviceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);

        var definitionPath = Path.Combine(serviceDirectory, ServiceGenerator.DefinitionFileName);
        var packagesPath = Path.Combine(serviceDirectory, PackagesFileName);

        if (!File.Exists(definitionPath))
        {
            throw new MsgException($"No hay {ServiceGenerator.DefinitionFileName} en '{Path.GetFullPath(serviceDirectory)}'. ¿Es un servicio generado con MSG?");
        }

        var definition = ServiceGenerator.Deserialize(File.ReadAllText(definitionPath));
        var props = File.Exists(packagesPath) ? File.ReadAllText(packagesPath) : string.Empty;

        var packages = MsgPackageVersion().Matches(props).Select(m => m.Groups["id"].Value).ToList();
        if (packages.Count == 0)
        {
            throw new MsgException(
                $"Este servicio (MSG {definition.MsgVersion}) no usa los paquetes Msg.ServiceDefaults: se generó antes de la 0.2.0 " +
                "con el código común copiado dentro. Regenera la base con 'msg new --from msg.json -o <carpeta-nueva>' " +
                "y mueve tus endpoints al nuevo proyecto.");
        }

        var updated = MsgPackageVersion().Replace(props, m => m.Groups["prefix"].Value + targetVersion + m.Groups["suffix"].Value);
        File.WriteAllText(packagesPath, updated);
        File.WriteAllText(definitionPath, ServiceGenerator.Serialize(definition with { MsgVersion = targetVersion }));

        return new UpgradeResult(definition.MsgVersion, targetVersion, packages);
    }
}
