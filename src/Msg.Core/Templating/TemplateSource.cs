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
