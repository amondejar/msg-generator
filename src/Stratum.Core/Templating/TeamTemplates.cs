using System.Text.Json;
using System.Text.RegularExpressions;

namespace Stratum.Core.Templating;

/// <summary>
/// A team's own conventions, versioned with its code in a <c>.msg</c> folder:
/// <list type="bullet">
/// <item><c>.stratum/templates/&lt;set&gt;/&lt;path&gt;</c> replaces Stratum's template with the same path, or adds a new file.
/// An empty file removes it. Sets: <c>service</c>, <c>entity</c> (vertical slice), <c>entity-clean</c>, <c>entity-tests</c>.</item>
/// <item><c>.stratum/config.json</c>: <c>{ "tokens": { "Company": "Acme" } }</c> → <c>{{Company}}</c> in any template.</item>
/// </list>
/// Found by walking up from the working folder, so one <c>.msg</c> at the repo root serves every service.
/// </summary>
public sealed partial class TeamTemplates
{
    public const string FolderName = ".stratum";
    public const string TemplatesFolder = "templates";
    public const string ConfigFile = "config.json";

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]*$")]
    private static partial Regex TokenName();

    private TeamTemplates(string root, IReadOnlyDictionary<string, string> tokens)
    {
        Root = root;
        Tokens = tokens;
    }

    /// <summary>The <c>.msg</c> folder.</summary>
    public string Root { get; }

    public string TemplatesRoot => Path.Combine(Root, TemplatesFolder);

    public IReadOnlyDictionary<string, string> Tokens { get; }

    /// <summary>Folder name before Stratum 0.22 (still read; rename it to <see cref="FolderName"/>).</summary>
    public const string LegacyFolderName = ".msg";

    /// <summary>Walks up from <paramref name="startDirectory"/> looking for a <c>.stratum</c> (or old <c>.msg</c>) folder. Null when there is none.</summary>
    public static TeamTemplates? Find(string startDirectory)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(startDirectory)); dir is not null; dir = dir.Parent)
        {
            foreach (var name in new[] { FolderName, LegacyFolderName })
            {
                var candidate = Path.Combine(dir.FullName, name);
                if (Directory.Exists(candidate)
                    && (Directory.Exists(Path.Combine(candidate, TemplatesFolder)) || File.Exists(Path.Combine(candidate, ConfigFile))))
                {
                    return Load(candidate);
                }
            }
        }

        return null;
    }

    public static TeamTemplates Load(string msgFolder)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        var configPath = Path.Combine(msgFolder, ConfigFile);

        if (File.Exists(configPath))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(configPath), new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

                if (json.RootElement.TryGetProperty("tokens", out var section))
                {
                    foreach (var token in section.EnumerateObject())
                    {
                        if (!TokenName().IsMatch(token.Name))
                        {
                            throw new StratumException($"{configPath}: el token '{token.Name}' no es válido (letras, números, - y _).");
                        }

                        tokens[token.Name] = token.Value.ValueKind == JsonValueKind.String ? token.Value.GetString()! : token.Value.GetRawText();
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new StratumException($"{configPath} no es JSON válido: {ex.Message}", ex);
            }
        }

        return new TeamTemplates(Path.GetFullPath(msgFolder), tokens);
    }

    /// <summary>Custom files of one template set, keyed by template path (forward slashes).</summary>
    public IReadOnlyDictionary<string, string> FilesFor(string templateSet)
    {
        var folder = Path.Combine(TemplatesRoot, templateSet);
        if (!Directory.Exists(folder))
        {
            return new Dictionary<string, string>();
        }

        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(folder, f).Replace('\\', '/'),
                File.ReadAllText,
                StringComparer.Ordinal);
    }

    /// <summary>Every customized template, as "set/path", for display.</summary>
    public IReadOnlyList<string> CustomizedFiles() =>
        Directory.Exists(TemplatesRoot)
            ? [.. Directory.EnumerateFiles(TemplatesRoot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(TemplatesRoot, f).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)]
            : [];
}
