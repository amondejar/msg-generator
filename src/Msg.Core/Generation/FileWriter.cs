namespace Msg.Core.Generation;

public static class FileWriter
{
    /// <summary>Writes generated files under <paramref name="outputDirectory"/>.</summary>
    /// <exception cref="MsgException">If the folder exists and is not empty and <paramref name="force"/> is false.</exception>
    public static void Write(IEnumerable<GeneratedFile> files, string outputDirectory, bool force)
    {
        ArgumentNullException.ThrowIfNull(files);
        var root = Path.GetFullPath(outputDirectory);

        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !force)
        {
            throw new MsgException($"La carpeta '{root}' ya existe y no está vacía. Usa --force para sobrescribir.");
        }

        foreach (var file in files)
        {
            var target = Path.GetFullPath(Path.Combine(root, file.Path));

            // Defensive: never write outside the output folder, whatever the template paths say.
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new MsgException($"Ruta fuera de la carpeta de salida: {file.Path}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, file.Content);
        }
    }
}
