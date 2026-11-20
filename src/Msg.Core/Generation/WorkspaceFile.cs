using System.Text.Json;
using System.Text.Json.Serialization;

namespace Msg.Core.Generation;

/// <summary>
/// msg.workspace.json at the root of a system: services that live outside that folder (another disk, a shared
/// repository...). Services inside the folder are found on their own and don't need to be listed.
/// </summary>
public sealed class WorkspaceFile
{
    public const string FileName = "msg.workspace.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Paths relative to the workspace (with "/") or absolute (another drive).</summary>
    [JsonPropertyName("services")]
    public List<string> Services { get; init; } = [];

    public static WorkspaceFile Load(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
        {
            return new WorkspaceFile();
        }

        try
        {
            return JsonSerializer.Deserialize<WorkspaceFile>(File.ReadAllText(path), Json) ?? new WorkspaceFile();
        }
        catch (JsonException ex)
        {
            throw new MsgException($"{FileName} no es válido: {ex.Message}");
        }
    }

    /// <summary>Full paths of the listed services that still exist (with msg.json).</summary>
    public static IReadOnlyList<string> ExternalServices(string root) =>
        [.. Load(root).Services
            .Select(s => Path.GetFullPath(Path.Combine(root, s)))
            .Where(d => File.Exists(Path.Combine(d, ServiceGenerator.DefinitionFileName)))
            .Distinct(StringComparer.Ordinal)];

    /// <summary>Lists a service folder outside the workspace. Folders inside it are found anyway and are not added.</summary>
    /// <returns>False when nothing had to be added.</returns>
    public static bool Add(string root, string serviceDirectory)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(serviceDirectory);
        if (!File.Exists(Path.Combine(full, ServiceGenerator.DefinitionFileName)))
        {
            throw new MsgException($"'{full}' no es un servicio de Stratum (no tiene msg.json).");
        }

        if (IsInside(rootFull, full))
        {
            return false;
        }

        var file = Load(rootFull);
        var entry = Entry(rootFull, full);
        if (file.Services.Any(s => Path.GetFullPath(Path.Combine(rootFull, s)) == full))
        {
            return false;
        }

        file.Services.Add(entry);
        Save(rootFull, file);
        return true;
    }

    /// <summary>Stops listing a service (its files are not touched).</summary>
    public static bool Remove(string root, string serviceDirectory)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, serviceDirectory));
        var file = Load(rootFull);
        var removed = file.Services.RemoveAll(s => Path.GetFullPath(Path.Combine(rootFull, s)) == full) > 0;
        if (removed)
        {
            Save(rootFull, file);
        }

        return removed;
    }

    public static bool IsInside(string root, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(directory));
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    /// <summary>Relative when on the same drive (the workspace can move with its neighbours), absolute otherwise.</summary>
    private static string Entry(string root, string full)
    {
        var relative = Path.GetRelativePath(root, full);
        return Path.IsPathRooted(relative) ? full.Replace('\\', '/') : relative.Replace('\\', '/');
    }

    private static void Save(string root, WorkspaceFile file) =>
        File.WriteAllText(Path.Combine(root, FileName), JsonSerializer.Serialize(file, Json) + "\n");
}
