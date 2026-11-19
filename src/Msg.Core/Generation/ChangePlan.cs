namespace Msg.Core.Generation;

public enum ChangeKind
{
    /// <summary>A file that doesn't exist yet. From then on it belongs to the user.</summary>
    Create,

    /// <summary>A *.g.cs file (or msg.json) owned by MSG, rewritten from msg.json.</summary>
    Regenerate,

    /// <summary>A user file that only gets missing entries added (packages, connection strings).</summary>
    Merge,
}

public sealed record FileChange(string Path, ChangeKind Kind, string Content, string? Previous);

/// <summary>Every change MSG would make in a service folder. Previewable, then applied as a whole.</summary>
public sealed record ChangePlan(string Directory, IReadOnlyList<FileChange> Changes, IReadOnlyList<string> Warnings)
{
    public void Apply()
    {
        foreach (var change in Changes)
        {
            var path = Path.Combine(Directory, change.Path);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, change.Content);
        }
    }
}
