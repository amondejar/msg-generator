using System.Text.RegularExpressions;
using Stratum.Core.Generation;
using Stratum.Core.Model;

namespace Stratum.Studio.State;

/// <summary>Detects the external tools the Studio drives. Results are cached until <see cref="Reset"/>.</summary>
public sealed class Toolchain
{
    private Task<bool>? _docker;
    private Task<bool>? _dotnetEf;

    /// <summary>Docker or Podman (with the docker CLI) running: needed for the databases in Aspire.</summary>
    public Task<bool> HasDockerAsync() => _docker ??= Check("docker", ["info"]);

    public Task<bool> HasDotnetEfAsync() => _dotnetEf ??= Check("dotnet", ["ef", "--version"]);

    public void Reset()
    {
        _docker = null;
        _dotnetEf = null;
    }

    private static async Task<bool> Check(string fileName, IReadOnlyList<string> arguments) =>
        (await ProcessRunner.RunAsync(fileName, arguments, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false)).ExitCode == 0;
}

/// <summary>Command lines for a service, with the right projects for its architecture.</summary>
public static partial class ServiceCommands
{
    public const string DashboardJob = "system:run";
    public const string InstallEfJob = "tool:dotnet-ef";

    /// <summary>The Aspire dashboard prints its login link once it is ready.</summary>
    [GeneratedRegex(@"https?://[^\s]+/login\?t=[^\s]+")]
    public static partial Regex DashboardUrl();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,99}$")]
    private static partial Regex MigrationName();

    public static string MigrationsJob(string serviceDirectory) => $"ef:{serviceDirectory}";

    public static bool IsValidMigrationName(string? name) => name is not null && MigrationName().IsMatch(name);

    public static string MigrationsProject(ServiceDefinition definition) => MigrationReviewer.MigrationsProject(definition);

    public static IReadOnlyList<string> AddMigration(ServiceDefinition definition, string name) => Ef(definition, "migrations", "add", name);

    public static IReadOnlyList<string> RemoveMigration(ServiceDefinition definition) => Ef(definition, "migrations", "remove");

    public static IReadOnlyList<string> HasPendingModelChanges(ServiceDefinition definition) => Ef(definition, "migrations", "has-pending-model-changes");

    /// <summary>SQL of one migration, from the previous one ("0" = empty database) to <paramref name="to"/>, written to <paramref name="outputFile"/>.</summary>
    public static IReadOnlyList<string> Script(ServiceDefinition definition, string from, string to, string outputFile) =>
        Ef(definition, "migrations", "script", from, to, "--output", outputFile);

    /// <summary>"dotnet ef ..." with the right projects for the architecture.</summary>
    private static List<string> Ef(ServiceDefinition definition, params string[] verb)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var args = new List<string> { "ef" };
        args.AddRange(verb);
        args.AddRange(["--project", MigrationsProject(definition)]);
        if (definition.Architecture == Architectures.CleanDdd)
        {
            args.AddRange(["--startup-project", $"src/{definition.Service.Name}.Api"]);
        }

        return args;
    }

    public static IReadOnlyList<string> Migrations(string serviceDirectory, ServiceDefinition definition) =>
        MigrationReviewer.List(serviceDirectory, definition);

    public static string SuggestMigrationName(IReadOnlyList<string> existing, ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(definition);

        if (existing.Count == 0)
        {
            return "Initial";
        }

        var candidate = definition.Entities.Count > 0 ? $"Add{definition.Entities[^1].Name}" : "Update";
        return existing.Any(m => m.EndsWith("_" + candidate, StringComparison.Ordinal))
            ? $"{candidate}{DateTime.Now:yyyyMMddHHmm}"
            : candidate;
    }
}
