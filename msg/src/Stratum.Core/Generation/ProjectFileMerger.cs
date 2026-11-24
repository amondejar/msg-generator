using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Stratum.Core.Generation;

/// <summary>
/// Edits of files the user owns: adds what Stratum would render but is missing, and removes only items Stratum itself
/// rendered for something that is now turned off. Pure functions over text, so changes can be previewed.
/// </summary>
internal static partial class ProjectFileMerger
{
    [GeneratedRegex("""<(?<item>PackageVersion|PackageReference|ProjectReference|Using)\s+Include="(?<id>[^"]+)"[^>]*/>""")]
    private static partial Regex Item();

    /// <summary>MSBuild items (PackageVersion / PackageReference / ProjectReference / Using).</summary>
    /// <returns>The merged text, or null when nothing is missing.</returns>
    public static string? MergeItems(string text, string rendered)
    {
        var existing = Item().Matches(text)
            .Select(m => Normalize(m.Groups["id"].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = Item().Matches(rendered)
            .Where(m => !existing.Contains(Normalize(m.Groups["id"].Value)))
            .GroupBy(m => m.Groups["item"].Value)
            .ToList();

        if (missing.Count == 0)
        {
            return null;
        }

        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        foreach (var group in missing)
        {
            var lines = string.Concat(group.Select(m => $"    {m.Value}{nl}"));

            // At the end of the last ItemGroup that already holds this kind of item; otherwise a new ItemGroup.
            var lastItem = text.LastIndexOf($"<{group.Key} ", StringComparison.Ordinal);
            var close = lastItem >= 0 ? text.IndexOf("</ItemGroup>", lastItem, StringComparison.Ordinal) : -1;
            if (close >= 0)
            {
                text = text.Insert(text.LastIndexOf('\n', close) + 1, lines);
            }
            else
            {
                var end = text.LastIndexOf("</Project>", StringComparison.Ordinal);
                text = text.Insert(end, $"  <ItemGroup>{nl}{lines}  </ItemGroup>{nl}{nl}");
            }
        }

        return text;
    }

    /// <summary>
    /// Removes the items Stratum rendered before (<paramref name="oldRendered"/>) but no longer renders
    /// (<paramref name="newRendered"/>), e.g. the OpenAPI packages when OpenAPI is turned off. Items the user added are kept.
    /// </summary>
    /// <returns>The new text, or null when nothing is removed.</returns>
    public static string? RemoveItems(string text, string oldRendered, string newRendered)
    {
        var keep = Item().Matches(newRendered)
            .Select(m => $"{m.Groups["item"].Value}|{Normalize(m.Groups["id"].Value)}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var obsolete = Item().Matches(oldRendered)
            .Select(m => $"{m.Groups["item"].Value}|{Normalize(m.Groups["id"].Value)}")
            .Where(k => !keep.Contains(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (obsolete.Count == 0)
        {
            return null;
        }

        var result = Regex.Replace(text, """[ \t]*<(?<item>PackageVersion|PackageReference|ProjectReference|Using)\s+Include="(?<id>[^"]+)"[^>]*/>[ \t]*\r?\n?""", m =>
            obsolete.Contains($"{m.Groups["item"].Value}|{Normalize(m.Groups["id"].Value)}") ? string.Empty : m.Value);

        // ItemGroups left empty.
        result = Regex.Replace(result, @"[ \t]*<ItemGroup>\s*</ItemGroup>[ \t]*\r?\n(?:[ \t]*\r?\n)?", string.Empty);
        return result == text ? null : result;
    }

    /// <summary>Adds the ConnectionStrings entries of <paramref name="rendered"/> missing in an appsettings file.</summary>
    /// <returns>The merged JSON, or null when nothing is missing.</returns>
    public static string? MergeConnectionStrings(string text, string rendered)
    {
        if (JsonNode.Parse(rendered)?["ConnectionStrings"] is not JsonObject renderedStrings)
        {
            return null;
        }

        var settings = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject ?? throw new StratumException("appsettings no es un objeto JSON.");

        if (settings["ConnectionStrings"] is not JsonObject strings)
        {
            strings = [];
            settings["ConnectionStrings"] = strings;
        }

        var changed = false;
        foreach (var (key, value) in renderedStrings)
        {
            if (!strings.ContainsKey(key))
            {
                strings[key] = value?.DeepClone();
                changed = true;
            }
        }

        return changed ? settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n" : null;
    }

    /// <summary>File-based helper used by "stratum aspire".</summary>
    public static bool Merge(string path, string rendered)
    {
        var merged = MergeItems(File.ReadAllText(path), rendered);
        if (merged is null)
        {
            return false;
        }

        File.WriteAllText(path, merged);
        return true;
    }

    private static string Normalize(string include) => include.Replace('\\', '/');
}
