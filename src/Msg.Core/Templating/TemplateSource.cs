using System.Reflection;

namespace Msg.Core.Templating;

public sealed record TemplateFile(string Path, string Content);

/// <summary>Reads the templates embedded in Msg.Core (see Msg.Core.csproj).</summary>
public static class TemplateSource
{
    public static IReadOnlyList<TemplateFile> Load(string templateName)
    {
        var assembly = typeof(TemplateSource).Assembly;
        var prefix = $"templates/{templateName}/";

        var files = assembly.GetManifestResourceNames()
            .Select(n => n.Replace('\\', '/'))
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n => new TemplateFile(n[prefix.Length..], Read(assembly, n)))
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
