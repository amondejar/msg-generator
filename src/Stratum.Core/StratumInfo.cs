using System.Reflection;

namespace Stratum.Core;

public static class StratumInfo
{
    /// <summary>
    /// Stratum version, taken from &lt;Version&gt; in Directory.Build.props (single source of truth).
    /// It is also the version of the Stratum.ServiceDefaults.* packages that generated services reference.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>
    /// Prefix of the package IDs: StratumKit.ServiceDefaults, StratumKit.Messaging… ("Stratum." is reserved by someone
    /// else on nuget.org). Services generated before 0.36.1 use Stratum.*; any change or upgrade renames them.
    /// </summary>
    public const string PackagePrefix = "StratumKit.";

    /// <summary>
    /// Built by the release workflow (-p:StratumPublished=true): the Stratum.* packages of this version are on
    /// nuget.org, so services need no local package source.
    /// </summary>
    public static bool IsPublished { get; } = typeof(StratumInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "StratumPublished" && a.Value == "true");

    private static string ReadVersion()
    {
        var informational = typeof(StratumInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        // The SDK appends "+<commit sha>"; that is not part of a NuGet version.
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? informational[..plus] : informational;
    }
}

public sealed class StratumException : Exception
{
    public StratumException() { }

    public StratumException(string message) : base(message) { }

    public StratumException(string message, Exception inner) : base(message, inner) { }
}
