using System.Reflection;

namespace Stratum.Core;

public static class StratumInfo
{
    /// <summary>
    /// Stratum version, taken from &lt;Version&gt; in Directory.Build.props (single source of truth).
    /// It is also the version of the Stratum.ServiceDefaults.* packages that generated services reference.
    /// </summary>
    public static string Version { get; } = ReadVersion();

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
