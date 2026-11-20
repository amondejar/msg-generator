namespace Msg.Core.Generation;

public enum ChangeKind
{
    /// <summary>A file that doesn't exist yet. If it isn't a *.g.cs file, from then on it belongs to the user.</summary>
    Create,

    /// <summary>A *.g.cs file (or msg.json) owned by MSG, rewritten from msg.json.</summary>
    Regenerate,

    /// <summary>A user file that only gets missing entries added (packages, connection strings).</summary>
    Merge,

    /// <summary>
    /// A user file rewritten by MSG. Only happens when the user explicitly converts an entity created
    /// before MSG 0.10 to the editable layout.
    /// </summary>
    Replace,

    /// <summary>
    /// A file removed: a *.g.cs that is no longer generated, or (only when the user asks for it)
    /// the user's files of a removed entity.
    /// </summary>
    Delete,
}

public sealed record FileChange(string Path, ChangeKind Kind, string Content, string? Previous)
{
    public bool IsGenerated => Path.EndsWith(".g.cs", StringComparison.Ordinal) || Path == ServiceGenerator.DefinitionFileName;

    /// <summary>A generated file the user edited by hand (its msg-hash doesn't match): regenerating loses those edits.</summary>
    public bool HandEdited { get; init; }

    /// <summary>True when the change rewrites or removes code written by the user.</summary>
    public bool TouchesUserCode => HandEdited || Kind is ChangeKind.Replace || (Kind is ChangeKind.Delete && !IsGenerated && Previous is not null && !IsUnchangedDelete);

    /// <summary>Set by the planner when a deleted file is exactly as MSG generated it (nothing of the user's is lost).</summary>
    public bool IsUnchangedDelete { get; init; }
}

/// <summary>Every change MSG would make in a service folder. Previewable, then applied as a whole.</summary>
public sealed record ChangePlan(string Directory, IReadOnlyList<FileChange> Changes, IReadOnlyList<string> Warnings)
{
    public void Apply()
    {
        foreach (var change in Changes)
        {
            var path = System.IO.Path.Combine(Directory, change.Path);
            if (change.Kind == ChangeKind.Delete)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                RemoveEmptyFolders(System.IO.Path.GetDirectoryName(path));
                continue;
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, change.Content);
        }
    }

    private void RemoveEmptyFolders(string? folder)
    {
        var root = System.IO.Path.GetFullPath(Directory);
        while (folder is not null
               && System.IO.Path.GetFullPath(folder).Length > root.Length
               && System.IO.Directory.Exists(folder)
               && !System.IO.Directory.EnumerateFileSystemEntries(folder).Any())
        {
            System.IO.Directory.Delete(folder);
            folder = System.IO.Path.GetDirectoryName(folder);
        }
    }
}
