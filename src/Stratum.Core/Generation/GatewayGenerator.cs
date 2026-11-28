using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Stratum.Core.Model;
using Stratum.Core.Templating;

namespace Stratum.Core.Generation;

/// <summary>An entity route of the gateway: /api/v1/{Route} → the service that has it.</summary>
public sealed record GatewayRoute(string Route, AspireService Service);

/// <summary>
/// The system's gateway ("stratum gateway"): a YARP project next to the AppHost, the only door for the frontend.
/// <list type="bullet">
/// <item>Its mode is the &lt;StratumGateway&gt; property of the AppHost: proxy (the frontend sends its own token) or bff
/// (the gateway signs the user in and keeps the tokens).</item>
/// <item>GatewayWiring.g.cs and GatewayRoutes.g.cs are regenerated with the AppHost ("stratum aspire"); the rest is
/// created once and is the user's. The .csproj only gets the missing packages added.</item>
/// </list>
/// </summary>
public static partial class GatewayGenerator
{
    public const string Proxy = "proxy";

    public const string Bff = "bff";

    public static readonly IReadOnlyList<string> Modes = [Proxy, Bff];

    public const string YarpVersion = "2.3.0";

    /// <summary>Same line as Microsoft.Extensions.ServiceDiscovery in the services.</summary>
    public const string ServiceDiscoveryYarpVersion = "10.11.0";

    /// <summary>Same as Microsoft.AspNetCore.Authentication.JwtBearer in StratumKit.ServiceDefaults.Authentication.</summary>
    public const string OpenIdConnectVersion = "10.0.15";

    [GeneratedRegex("<StratumGateway>(?<m>[^<]*)</StratumGateway>")]
    private static partial Regex Property();

    /// <summary>The gateway's mode (the AppHost's &lt;StratumGateway&gt;), or null when the system has none.</summary>
    public static string? Mode(string root, string appHostName)
    {
        var csproj = Path.Combine(root, $"{appHostName}.AppHost", $"{appHostName}.AppHost.csproj");
        if (!File.Exists(csproj) || Property().Match(File.ReadAllText(csproj)) is not { Success: true } match)
        {
            return null;
        }

        var mode = match.Groups["m"].Value.Trim().ToLowerInvariant();
        return Modes.Contains(mode) ? mode : null;
    }

    /// <summary>"Shop.Gateway": its folder and project, next to the AppHost.</summary>
    public static string ProjectName(string appHostName) => $"{appHostName}.Gateway";

    /// <summary>Its name in Aspire: "gateway", or "&lt;system&gt;-gateway" when a service is called Gateway.</summary>
    public static string ResourceName(string appHostName, IReadOnlyList<AspireService> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.Any(s => s.ResourceName == "gateway") ? $"{ServiceName.ToKebab(appHostName)}-gateway" : "gateway";
    }

    /// <summary>
    /// /api/v1/&lt;entities&gt; of every service, and the routes two services have (left out: they're only reachable
    /// with /services/&lt;name&gt;/api/v1/...).
    /// </summary>
    public static (IReadOnlyList<GatewayRoute> Routes, IReadOnlyList<string> Conflicts) Routes(IReadOnlyList<AspireService> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var all = services
            .SelectMany(s => s.Definition.Entities.Select(e => new GatewayRoute(EntityCodeGen.Kebab(e.Plural), s)))
            .GroupBy(r => r.Route, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        var routes = all.Where(g => g.Select(r => r.Service.ResourceName).Distinct().Count() == 1).Select(g => g.First()).ToList();
        var conflicts = all.Where(g => g.Select(r => r.Service.ResourceName).Distinct().Count() > 1)
            .Select(g => $"/api/v1/{g.Key} está en {string.Join(" y ", g.Select(r => r.Service.Name).Distinct())}: el gateway no sabe a cuál llevarla. " +
                         $"Llega con /services/<servicio>/api/v1/{g.Key}, o cambia el plural de una de las entidades.")
            .ToList();
        return (routes, conflicts);
    }

    /// <summary>Creates or updates the gateway project; returns the notes for the user (routes left out).</summary>
    internal static IReadOnlyList<string> Generate(
        string root, string appHostName, IReadOnlyList<AspireService> services, string mode, List<string> created, List<string> updated)
    {
        if (!Modes.Contains(mode))
        {
            throw new StratumException($"No conozco el modo de gateway '{mode}'. Modos: {Proxy} (el frontend manda su token) o {Bff} (el gateway guarda la sesión).");
        }

        var project = ProjectName(appHostName);
        var (routes, conflicts) = Routes(services);
        var resource = ResourceName(appHostName, services);
        var port = ServiceName.DevPort(appHostName + "Gateway");
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["System"] = appHostName,
            ["gateway-resource"] = resource,
            ["GatewayMode"] = mode,
            ["GatewayModeComment"] = mode == Bff ? " and the frontend's session (BFF)" : string.Empty,
            ["StratumVersion"] = StratumInfo.Version,
            ["YarpVersion"] = YarpVersion,
            ["ServiceDiscoveryVersion"] = ServiceDiscoveryYarpVersion,
            ["OpenIdConnectVersion"] = OpenIdConnectVersion,
            ["GatewayPort"] = port.ToString(CultureInfo.InvariantCulture),
            ["GatewayHttpsPort"] = (port + 1000).ToString(CultureInfo.InvariantCulture),
            ["GatewayServices"] = string.Join("\n", services.OrderBy(s => s.ResourceName, StringComparer.Ordinal)
                .Select(s => $"        [\"{s.ResourceName}\"] = \"https+http://{s.ResourceName}\",")),
            ["GatewayEntityRoutes"] = string.Join("\n", routes.Select(r => $"        (\"{r.Route}\", \"{r.Service.ResourceName}\"),")),
        };

        string Render(TemplateFile file, string forMode) =>
            TemplateEngine.Render(file.Content, tokens, feature => feature switch
            {
                "bff" => forMode == Bff,
                _ => throw new StratumException($"Condición desconocida '{feature}' en la plantilla gateway/{file.Path}."),
            }, $"gateway/{file.Path}");

        foreach (var file in TemplateSource.LoadEmbedded("gateway"))
        {
            var relative = file.Path.Replace("__System__", appHostName, StringComparison.Ordinal);
            var path = Path.Combine(root, relative);
            var content = Render(file, mode);
            if (relative.EndsWith(".g.cs", StringComparison.Ordinal))
            {
                Write(path, relative, content, created, updated, overwrite: true);
            }
            else if (relative.EndsWith(".csproj", StringComparison.Ordinal) && File.Exists(path))
            {
                // Packages: the missing ones are added, the other mode's go (OpenID Connect), Stratum's are brought up to this version.
                var existing = File.ReadAllText(path);
                var text = ProjectFileMerger.MergeItems(existing, content) ?? existing;
                text = ProjectFileMerger.RemoveItems(text, Render(file, mode == Bff ? Proxy : Bff), content) ?? text;
                text = StratumPackageVersions(text);
                if (text != existing)
                {
                    File.WriteAllText(path, text);
                    updated.Add(relative);
                }
            }
            else
            {
                Write(path, relative, content, created, updated, overwrite: false);
            }
        }

        return conflicts;
    }

    /// <summary>StratumKit.* packages of an older version → this one (never down).</summary>
    private static string StratumPackageVersions(string csproj) =>
        Regex.Replace(csproj, "(<PackageReference\\s+Include=\"StratumKit\\.[^\"]+\"\\s+Version=\")(?<v>[^\"]+)(\")", m =>
            Version.TryParse(m.Groups["v"].Value.Split('-')[0], out var current)
            && Version.TryParse(StratumInfo.Version.Split('-')[0], out var stratum)
            && current < stratum
                ? m.Groups[1].Value + StratumInfo.Version + m.Groups[3].Value
                : m.Value);

    private static void Write(string path, string relative, string content, List<string> created, List<string> updated, bool overwrite)
    {
        var existed = File.Exists(path);
        if (existed && (!overwrite || File.ReadAllText(path) == content))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        (existed ? updated : created).Add(relative);
    }

    /// <summary>The gateway's lines in StratumServices.g.cs: it reaches every service, and it's the system's public address.</summary>
    internal static string AppHostLines(string appHostName, IReadOnlyList<AspireService> services, string variable, Func<AspireService, string> serviceVariable)
    {
        var sb = new StringBuilder();
        sb.Append("        // The gateway (\"stratum gateway\"): the only door for the frontend. /api/v1/<entities> reaches the service\n")
          .Append("        // that has them, /services/<name>/... any service; deployed, it's the only public address.\n")
          .Append($"        var {variable} = builder.AddProject<Projects.{ProjectName(appHostName).Replace('.', '_')}>(\"{ResourceName(appHostName, services)}\")\n");
        foreach (var service in services)
        {
            sb.Append($"            .WithReference({serviceVariable(service)})\n");
        }

        sb.Append("            .WithExternalHttpEndpoints()\n")
          .Append("            .WithHttpHealthCheck(\"/health/ready\");\n\n");
        return sb.ToString();
    }
}
