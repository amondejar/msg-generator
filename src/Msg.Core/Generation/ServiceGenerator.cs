using System.Text.Json;
using Msg.Core.Model;
using Msg.Core.Templating;

namespace Msg.Core.Generation;

public sealed record GeneratedFile(string Path, string Content);

/// <summary>
/// Turns a <see cref="ServiceDefinition"/> into files. It is pure (no disk I/O) on purpose:
/// the CLI writes the result to a folder, and the visual editor will be able to zip it,
/// preview it or push it to a repo using exactly the same code.
/// </summary>
public static class ServiceGenerator
{
    public const string TemplateName = "service";
    public const string DefinitionFileName = "msg.json";

    // Whole files/folders that only exist when a feature is enabled.
    // Paths are template paths (before renaming), compared by prefix.
    private static readonly (string Prefix, string Feature)[] ConditionalPaths =
    [
        ("tests/", "tests"),
        ("Dockerfile", "docker"),
        ("_dockerignore", "docker"),
        ("_github/", "ci"),
    ];

    public static IReadOnlyList<GeneratedFile> Generate(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ServiceName.Validate(definition.Service.Name);

        var tokens = BuildTokens(definition);
        var features = definition.Features;
        var result = new List<GeneratedFile>();

        foreach (var template in TemplateSource.Load(TemplateName))
        {
            if (!IsIncluded(template.Path, features))
            {
                continue;
            }

            var path = RenderPath(template.Path, tokens);
            var content = TemplateEngine.Render(template.Content, tokens, features.IsEnabled, template.Path);

            // A template wrapped entirely in "@msg:if feature" renders empty when the feature is off: skip the file.
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            result.Add(new GeneratedFile(path, content));
        }

        result.Add(new GeneratedFile(DefinitionFileName, Serialize(definition)));
        return result;
    }

    public static Dictionary<string, string> BuildTokens(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var name = definition.Service.Name;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Service"] = name,
            ["service-kebab"] = ServiceName.ToKebab(name),
            ["DevPort"] = ServiceName.DevPort(name).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["MsgVersion"] = MsgInfo.Version,
        };
    }

    public static string Serialize(ServiceDefinition definition) =>
        JsonSerializer.Serialize(definition, MsgJsonContext.Default.ServiceDefinition) + "\n";

    public static ServiceDefinition Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, MsgJsonContext.Default.ServiceDefinition)
                ?? throw new MsgException("msg.json está vacío.");
        }
        catch (JsonException ex)
        {
            throw new MsgException($"msg.json no es válido: {ex.Message}", ex);
        }
    }

    private static bool IsIncluded(string templatePath, FeatureSet features)
    {
        foreach (var (prefix, feature) in ConditionalPaths)
        {
            if (templatePath.StartsWith(prefix, StringComparison.Ordinal) && !features.IsEnabled(feature))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Path segments starting with a single underscore become dotfiles (<c>_gitignore</c> → <c>.gitignore</c>,
    /// <c>_github</c> → <c>.github</c>); this avoids hidden files inside the template folder.
    /// Tokens use <c>__Service__</c> in paths.
    /// </summary>
    private static string RenderPath(string templatePath, Dictionary<string, string> tokens)
    {
        var segments = templatePath.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            foreach (var (key, value) in tokens)
            {
                segment = segment.Replace("__" + key + "__", value, StringComparison.Ordinal);
            }

            if (segment.Length > 1 && segment[0] == '_' && segment[1] != '_')
            {
                segment = "." + segment[1..];
            }

            segments[i] = segment;
        }

        return string.Join('/', segments);
    }
}
