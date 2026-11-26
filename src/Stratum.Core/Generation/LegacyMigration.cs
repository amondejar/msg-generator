using System.Text.RegularExpressions;

namespace Stratum.Core.Generation;

/// <summary>One file the migration touches.</summary>
/// <param name="NewPath">Different from <paramref name="Path"/> when the file is renamed; null when it is deleted.</param>
public sealed record MigrationEdit(string Path, string? NewPath, string? Content, string Reason);

/// <summary>
/// Moves services generated before Stratum 0.22 (msg.json, Msg.* packages, MsgWiring.g.cs, .msg) to the Stratum names.
/// Rewrites the names in the user's code too (usings, AddMsgWiring(), package extension methods), because those
/// files reference the packages. Generated *.g.cs files are not rewritten: they are regenerated afterwards.
/// Table names (msg_outbox, msg_inbox) and message headers (msg-source...) stay as they are on purpose: existing
/// databases and services on other versions keep working.
/// </summary>
public static partial class LegacyMigration
{
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules", "artifacts", "TestResults",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".props", ".targets", ".slnx", ".sln", ".json", ".md", ".yml", ".yaml", ".razor", ".editorconfig", ".config",
    };

    [GeneratedRegex(@"\bMsg\.(?=(ServiceDefaults|Ddd|Messaging)\b)")]
    private static partial Regex PackageNamespace();

    [GeneratedRegex(@"\b(Add|Use|Map)Msg(?=[A-Z]\w*\s*[(<])")]
    private static partial Regex ExtensionMethod();

    [GeneratedRegex(@"\bMsg(?=(Wiring|AuthOptions|ServiceResources|Services)\b)")]
    private static partial Regex TypeName();

    [GeneratedRegex(@"\bmsg\.json\b")]
    private static partial Regex DefinitionMention();

    /// <summary>The services under <paramref name="path"/> (or the service itself) that still use the old names.</summary>
    public static IReadOnlyList<string> LegacyServices(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (ServiceGenerator.IsLegacyService(full))
        {
            return [full];
        }

        if (ServiceGenerator.FindDefinition(full) is not null)
        {
            return [];
        }

        var found = new List<string>();
        Walk(full, 0);
        foreach (var external in WorkspaceFile.ExternalServices(full).Where(ServiceGenerator.IsLegacyService))
        {
            if (!found.Contains(external))
            {
                found.Add(external);
            }
        }

        return found;

        void Walk(string directory, int depth)
        {
            if (directory != full && ServiceGenerator.FindDefinition(directory) is not null)
            {
                if (ServiceGenerator.IsLegacyService(directory))
                {
                    found.Add(directory);
                }

                return;
            }

            if (depth >= 3)
            {
                return;
            }

            foreach (var child in AspireGenerator.Subdirectories(directory).Where(d => !SkippedFolders.Contains(System.IO.Path.GetFileName(d))))
            {
                Walk(child, depth + 1);
            }
        }
    }

    /// <summary>Edits for one service folder (absolute paths).</summary>
    public static IReadOnlyList<MigrationEdit> PlanService(string serviceDirectory)
    {
        var dir = System.IO.Path.GetFullPath(serviceDirectory);
        var edits = new List<MigrationEdit>();
        var legacyDefinition = System.IO.Path.Combine(dir, ServiceGenerator.LegacyDefinitionFileName);
        if (File.Exists(legacyDefinition))
        {
            var definition = ServiceGenerator.Deserialize(File.ReadAllText(legacyDefinition));
            edits.Add(new MigrationEdit(legacyDefinition, System.IO.Path.Combine(dir, ServiceGenerator.DefinitionFileName),
                ServiceGenerator.Serialize(definition), "msg.json → stratum.json"));
        }

        foreach (var file in Files(dir))
        {
            var name = System.IO.Path.GetFileName(file);
            if (name == "MsgWiring.g.cs")
            {
                edits.Add(new MigrationEdit(file, null, null, "sustituido por StratumWiring.g.cs (se genera de nuevo)"));
                continue;
            }

            if (name.EndsWith(".g.cs", StringComparison.Ordinal) || name == ServiceGenerator.LegacyDefinitionFileName)
            {
                continue;   // regenerated afterwards
            }

            if (Rewrite(file) is { } edit)
            {
                edits.Add(edit);
            }
        }

        return edits;
    }

    /// <summary>System-level edits: AppHost, msg.workspace.json and the team's .msg folder.</summary>
    public static IReadOnlyList<MigrationEdit> PlanSystem(string root)
    {
        var full = System.IO.Path.GetFullPath(root);
        var edits = new List<MigrationEdit>();

        var workspace = System.IO.Path.Combine(full, WorkspaceFile.LegacyFileName);
        if (File.Exists(workspace) && !File.Exists(System.IO.Path.Combine(full, WorkspaceFile.FileName)))
        {
            edits.Add(new MigrationEdit(workspace, System.IO.Path.Combine(full, WorkspaceFile.FileName), File.ReadAllText(workspace),
                "msg.workspace.json → stratum.workspace.json"));
        }

        foreach (var appHost in Directory.EnumerateDirectories(full, "*.AppHost"))
        {
            foreach (var file in Files(appHost))
            {
                if (System.IO.Path.GetFileName(file) == "MsgServices.g.cs")
                {
                    edits.Add(new MigrationEdit(file, null, null, "sustituido por StratumServices.g.cs (se genera de nuevo)"));
                }
                else if (!file.EndsWith(".g.cs", StringComparison.Ordinal) && Rewrite(file) is { } edit)
                {
                    edits.Add(edit);
                }
            }
        }

        // Team templates: same rules, and the files named after the old wiring follow the new names.
        var team = System.IO.Path.Combine(full, ".msg");
        if (Directory.Exists(team) && !Directory.Exists(System.IO.Path.Combine(full, ".stratum")))
        {
            foreach (var file in Directory.EnumerateFiles(team, "*", SearchOption.AllDirectories))
            {
                var relative = System.IO.Path.GetRelativePath(team, file);
                var target = System.IO.Path.Combine(full, ".stratum", relative.Replace("MsgWiring.g.cs", "StratumWiring.g.cs", StringComparison.Ordinal));
                var text = File.ReadAllText(file);
                edits.Add(new MigrationEdit(file, target, RewriteText(text).Replace("@msg:", "@stratum:", StringComparison.Ordinal), ".msg → .stratum"));
            }
        }

        return edits;
    }

    /// <summary>Writes the edits: renamed files are written at their new path and the old one is deleted.</summary>
    public static void Apply(IEnumerable<MigrationEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        foreach (var edit in edits)
        {
            if (edit.Content is not null)
            {
                var target = edit.NewPath ?? edit.Path;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.WriteAllText(target, edit.Content);
            }

            if (edit.NewPath != edit.Path)
            {
                File.Delete(edit.Path);
            }
        }

        // Empty folders left by a moved .msg.
        foreach (var dir in edits.Where(e => e.NewPath is not null && e.NewPath != e.Path)
                     .Select(e => System.IO.Path.GetDirectoryName(e.Path)!).Distinct().OrderByDescending(d => d.Length))
        {
            for (var current = dir; Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any(); current = System.IO.Path.GetDirectoryName(current)!)
            {
                Directory.Delete(current);
                if (System.IO.Path.GetFileName(current) == ".msg")
                {
                    break;
                }
            }
        }
    }

    /// <summary>The old names in a user file → the Stratum ones.</summary>
    public static string RewriteText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = PackageNamespace().Replace(text, "Stratum.");
        result = result.Replace("pattern=\"Msg.*\"", "pattern=\"Stratum.*\"", StringComparison.Ordinal);   // nuget.config source mapping
        result = ExtensionMethod().Replace(result, "$1Stratum");
        result = TypeName().Replace(result, "Stratum");
        return DefinitionMention().Replace(result, "stratum.json");
    }

    private static MigrationEdit? Rewrite(string file)
    {
        var text = File.ReadAllText(file);
        var rewritten = RewriteText(text);
        return rewritten == text ? null : new MigrationEdit(file, file, rewritten, "nombres Msg → Stratum");
    }

    private static IEnumerable<string> Files(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root))
        {
            var name = System.IO.Path.GetFileName(file);
            if (TextExtensions.Contains(System.IO.Path.GetExtension(file)) || name is ".editorconfig" or "Dockerfile")
            {
                yield return file;
            }
        }

        foreach (var child in AspireGenerator.Subdirectories(root).Where(d => !SkippedFolders.Contains(System.IO.Path.GetFileName(d))))
        {
            foreach (var file in Files(child))
            {
                yield return file;
            }
        }
    }
}
