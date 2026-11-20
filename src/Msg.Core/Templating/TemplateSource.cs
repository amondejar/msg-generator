using System.Reflection;

namespace Msg.Core.Templating;

public sealed record TemplateFile(string Path, string Content, bool IsCustom = false);

/// <summary>Reads the templates embedded in Msg.Core (see Msg.Core.csproj), merged with the team's own.</summary>
public static class TemplateSource
{
    public static IReadOnlyList<string> Sets { get; } = ["service", "entity", "entity-clean", "entity-tests"];

    /// <summary>MSG's templates for a set, with the team's files replacing or adding to them.</summary>
    public static IReadOnlyList<TemplateFile> Load(string templateName, TeamTemplates? team = null)
    {
        var files = LoadEmbedded(templateName).ToDictionary(f => f.Path, StringComparer.Ordinal);

        if (team is not null)
        {
            foreach (var (path, content) in team.FilesFor(templateName))
            {
                files[path] = new TemplateFile(path, content, IsCustom: true);
            }
        }

        return [.. files.Values.OrderBy(f => f.Path, StringComparer.Ordinal)];
    }

    /// <summary>The user's half of a partial class (X.cs next to X.g.cs on purpose). Older templates had no partial classes.</summary>
    private static bool IsPartialStub(string content) => content.Contains(" partial ", StringComparison.Ordinal);

    private static readonly HashSet<string> RemovedTemplates = new(StringComparer.Ordinal)
    {
        // 0.14: replaced by ApiFactory.g.cs (database and broker replacements in one file).
        "service/tests/__Service__.Api.Tests/ApiFactory.Database.g.cs",
    };

    /// <summary>MSG's own templates only (what "msg templates eject" copies).</summary>
    public static IReadOnlyList<TemplateFile> LoadEmbedded(string templateName)
    {
        var assembly = typeof(TemplateSource).Assembly;
        var prefix = $"templates/{templateName}/";

        var files = assembly.GetManifestResourceNames()
            .Where(n => n.Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n => new TemplateFile(n.Replace('\\', '/')[prefix.Length..], Read(assembly, n)))
            .ToList();

        if (files.Count == 0)
        {
            throw new MsgException($"No se encontró la plantilla '{templateName}'.");
        }

        // X.cs next to X.g.cs: a template of an older MSG left behind (e.g. a zip unpacked over another version).
        var stale = files
            .Where(f => f.Path.EndsWith(".g.cs", StringComparison.Ordinal)
                        && files.Any(o => o.Path == f.Path[..^".g.cs".Length] + ".cs" && !IsPartialStub(o.Content)))
            .Select(f => f.Path[..^".g.cs".Length] + ".cs")
            .ToList();
        // Templates that older versions had and this one removed (a zip unpacked over the repo doesn't delete them).
        stale.AddRange(files.Select(f => $"{templateName}/{f.Path}").Where(RemovedTemplates.Contains).Select(p => p[(templateName.Length + 1)..]));
        if (stale.Count > 0)
        {
            throw new MsgException(
                $"La plantilla '{templateName}' mezcla archivos de dos versiones de MSG: {string.Join(", ", stale)} " +
                $"{(stale.Count == 1 ? "es" : "son")} de una versión anterior y ya no se usa. Borra la carpeta templates del repositorio de MSG, " +
                "vuelve a copiar la de esta versión y recompila.");
        }

        return files;
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new MsgException($"Recurso de plantilla no encontrado: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
