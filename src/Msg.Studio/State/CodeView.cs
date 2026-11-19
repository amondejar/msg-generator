using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Msg.Studio.State;

public enum DiffKind
{
    Same,
    Added,
    Removed,
}

public sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>Tiny helpers for the code panels: syntax highlighting (HTML-encoded) and a line diff.</summary>
public static partial class CodeView
{
    [GeneratedRegex("""(?<c>//[^\n]*|<!--.*?-->)|(?<s>"(?:[^"\\\n]|\\.)*")|(?<k>\b(?:using|namespace|public|private|internal|protected|static|sealed|partial|class|record|interface|var|new|return|await|async|if|else|foreach|in|is|null|true|false|this|void|string|int|long|decimal|bool|readonly|override|get|set|init|throw|typeof)\b)|(?<t>\b[A-Z][A-Za-z0-9]*(?=[<(\s]|$))""", RegexOptions.Multiline)]
    private static partial Regex Token();

    public static string Highlight(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var sb = new StringBuilder(code.Length * 2);
        var last = 0;

        foreach (Match m in Token().Matches(code))
        {
            sb.Append(WebUtility.HtmlEncode(code[last..m.Index]));
            var css = m.Groups["c"].Success ? "tok-c" : m.Groups["s"].Success ? "tok-s" : m.Groups["k"].Success ? "tok-k" : "tok-t";
            sb.Append("<span class=\"").Append(css).Append("\">").Append(WebUtility.HtmlEncode(m.Value)).Append("</span>");
            last = m.Index + m.Length;
        }

        return sb.Append(WebUtility.HtmlEncode(code[last..])).ToString();
    }

    /// <summary>LCS line diff. Files here are small (hundreds of lines), so O(n·m) is fine.</summary>
    public static IReadOnlyList<DiffLine> Diff(string? before, string after)
    {
        ArgumentNullException.ThrowIfNull(after);
        var a = (before ?? string.Empty).ReplaceLineEndings("\n").Split('\n');
        var b = after.ReplaceLineEndings("\n").Split('\n');

        if (before is null)
        {
            return [.. b.Select(l => new DiffLine(DiffKind.Added, l))];
        }

        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var result = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                result.Add(new DiffLine(DiffKind.Same, a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add(new DiffLine(DiffKind.Removed, a[x++]));
            }
            else
            {
                result.Add(new DiffLine(DiffKind.Added, b[y++]));
            }
        }

        result.AddRange(a[x..].Select(l => new DiffLine(DiffKind.Removed, l)));
        result.AddRange(b[y..].Select(l => new DiffLine(DiffKind.Added, l)));
        return result;
    }
}
