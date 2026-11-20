using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Msg.Core.Generation;

/// <summary>
/// Last line of every *.g.cs: a hash of its content. If the content no longer matches, someone edited the
/// generated file by hand and MSG warns before overwriting it.
/// </summary>
public static partial class GeneratedFileHash
{
    private const string Prefix = "// msg-hash: ";

    [GeneratedRegex(@"\r?\n// msg-hash: (?<hash>[0-9a-f]{16})\r?\n?$")]
    private static partial Regex Footer();

    public static string Stamp(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var body = content.EndsWith('\n') ? content : content + "\n";
        return $"{body}{Prefix}{Hash(body)}\n";
    }

    /// <summary>True when the file has a footer and its content changed since MSG wrote it. Files without footer (older MSG): false.</summary>
    public static bool IsHandEdited(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var match = Footer().Match(content);
        if (!match.Success)
        {
            return false;
        }

        var body = content[..(match.Index)] + "\n";
        return Hash(body) != match.Groups["hash"].Value;
    }

    private static string Hash(string body)
    {
        var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }
}
