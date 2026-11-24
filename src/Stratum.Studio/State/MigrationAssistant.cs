using Stratum.Core;
using Stratum.Core.Generation;

using static Stratum.Studio.Localization.I18n;

namespace Stratum.Studio.State;

public enum ModelCheck
{
    Unknown,
    Checking,
    UpToDate,
    Pending,
    Failed,
}

/// <summary>What the Studio knows about the migrations of one service.</summary>
public sealed class MigrationStatus
{
    public ModelCheck Check { get; internal set; }

    /// <summary>Output of the last failed check (usually a build error).</summary>
    public string? CheckOutput { get; internal set; }

    /// <summary>Review of a migration created (or reviewed) from the Studio. Keeps the renames it applied.</summary>
    public MigrationReview? Review { get; internal set; }

    public string? Sql { get; internal set; }

    public string? SqlFor { get; internal set; }

    public bool SqlLoading { get; internal set; }

    public string? Error { get; internal set; }
}

/// <summary>
/// Guided migrations (singleton, survives navigation): checks for model changes, creates the migration and
/// reviews it (renames → RenameColumn, data-loss warnings), shows its SQL and undoes it.
/// </summary>
public sealed class MigrationAssistant(ProcessRunner runner)
{
    private readonly Dictionary<string, MigrationStatus> _status = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public event Action? Changed;

    public MigrationStatus Get(string serviceDirectory)
    {
        lock (_lock)
        {
            if (!_status.TryGetValue(serviceDirectory, out var status))
            {
                status = new MigrationStatus();
                _status[serviceDirectory] = status;
            }

            return status;
        }
    }

    public static string CreateJob(string serviceDirectory) => ServiceCommands.MigrationsJob(serviceDirectory);

    /// <summary>
    /// "dotnet ef" needs the projects restored (obj/project.assets.json) before it can even read them: a service that
    /// was never built (just created, just cloned) is restored first. Null when everything is ready.
    /// </summary>
    private static async Task<string?> RestoreIfNeededAsync(string serviceDirectory)
    {
        var src = Path.Combine(serviceDirectory, "src");
        var missing = Directory.Exists(src) && Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Any(project => !File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json")));
        if (!missing)
        {
            return null;
        }

        var (exitCode, output) = await ProcessRunner.RunAsync("dotnet", ["restore"], serviceDirectory, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        return exitCode == 0 ? null : L("No se pudieron restaurar los paquetes (dotnet restore):", "The packages couldn't be restored (dotnet restore):") + "\n" + Tail(output);
    }

    public static string RemoveJob(string serviceDirectory) => $"ef-remove:{serviceDirectory}";

    /// <summary>"dotnet ef migrations has-pending-model-changes" (builds the service, so it takes a while).</summary>
    public async Task CheckAsync(string serviceDirectory)
    {
        var status = Get(serviceDirectory);
        if (status.Check == ModelCheck.Checking)
        {
            return;
        }

        status.Check = ModelCheck.Checking;
        Notify();

        var definition = ServicePlanner.Load(serviceDirectory);
        if (await RestoreIfNeededAsync(serviceDirectory).ConfigureAwait(false) is { } restoreError)
        {
            (status.Check, status.CheckOutput) = (ModelCheck.Failed, restoreError);
            Notify();
            return;
        }

        var (exitCode, output) = await ProcessRunner.RunAsync(
            "dotnet", ServiceCommands.HasPendingModelChanges(definition), serviceDirectory, TimeSpan.FromMinutes(5)).ConfigureAwait(false);

        // EF prints "No changes have been made to the model..." (exit 0) or "Changes have been made..." (exit 1).
        (status.Check, status.CheckOutput) =
            exitCode == 0 && output.Contains("No changes", StringComparison.OrdinalIgnoreCase) ? (ModelCheck.UpToDate, null)
            : output.Contains("Changes have been made", StringComparison.OrdinalIgnoreCase) ? (ModelCheck.Pending, null)
            : exitCode == 0 ? (ModelCheck.UpToDate, null)
            : (ModelCheck.Failed, Tail(output));
        Notify();
    }

    /// <summary>Creates the migration, then reviews and fixes it (pending renames become RenameColumn).</summary>
    public async Task CreateAsync(string serviceDirectory, string name)
    {
        var status = Get(serviceDirectory);
        var definition = ServicePlanner.Load(serviceDirectory);
        status.Error = null;
        status.Sql = null;
        if (await RestoreIfNeededAsync(serviceDirectory).ConfigureAwait(false) is { } restoreError)
        {
            status.Error = restoreError;
            Notify();
            return;
        }

        var job = runner.Start(CreateJob(serviceDirectory), L($"Migración {name}", $"Migration {name}"), serviceDirectory, "dotnet", ServiceCommands.AddMigration(definition, name));
        await job.Completion.ConfigureAwait(false);
        if (job.Status != JobStatus.Succeeded)
        {
            Notify();
            return;
        }

        try
        {
            status.Review = MigrationReviewer.ReviewLatest(serviceDirectory, apply: true);
            status.Check = ModelCheck.UpToDate;
        }
        catch (StratumException ex)
        {
            status.Error = ex.Message;
        }

        Notify();
    }

    /// <summary>Reviews (and fixes) the newest migration when it was created outside the Studio.</summary>
    public void ReviewLatest(string serviceDirectory)
    {
        var status = Get(serviceDirectory);
        try
        {
            status.Review = MigrationReviewer.ReviewLatest(serviceDirectory, apply: true);
            status.Error = null;
        }
        catch (StratumException ex)
        {
            status.Error = ex.Message;
        }

        Notify();
    }

    public async Task LoadSqlAsync(string serviceDirectory)
    {
        var status = Get(serviceDirectory);
        var definition = ServicePlanner.Load(serviceDirectory);
        var migrations = MigrationReviewer.List(serviceDirectory, definition);
        if (migrations.Count == 0 || status.SqlLoading)
        {
            return;
        }

        status.SqlLoading = true;
        Notify();

        if (await RestoreIfNeededAsync(serviceDirectory).ConfigureAwait(false) is { } restoreError)
        {
            status.SqlLoading = false;
            status.Error = restoreError;
            Notify();
            return;
        }

        var from = migrations.Count > 1 ? migrations[^2] : "0";
        var file = Path.Combine(Path.GetTempPath(), $"msg-{Guid.NewGuid():N}.sql");
        var (exitCode, output) = await ProcessRunner.RunAsync(
            "dotnet", ServiceCommands.Script(definition, from, migrations[^1], file), serviceDirectory, TimeSpan.FromMinutes(5)).ConfigureAwait(false);

        status.SqlLoading = false;
        status.SqlFor = migrations[^1];
        status.Sql = exitCode == 0 && File.Exists(file) ? (await File.ReadAllTextAsync(file).ConfigureAwait(false)).Trim() : null;
        if (File.Exists(file))
        {
            File.Delete(file);
        }

        status.Error = exitCode == 0 ? null : L("No se pudo generar el SQL:", "The SQL couldn't be generated:") + "\n" + Tail(output);
        Notify();
    }

    /// <summary>"dotnet ef migrations remove". The renames the migration performed go back to stratum.json.</summary>
    public async Task RemoveLatestAsync(string serviceDirectory)
    {
        var status = Get(serviceDirectory);
        var definition = ServicePlanner.Load(serviceDirectory);
        var migrations = MigrationReviewer.List(serviceDirectory, definition);
        if (migrations.Count == 0)
        {
            return;
        }

        var removed = migrations[^1];
        if (await RestoreIfNeededAsync(serviceDirectory).ConfigureAwait(false) is { } restoreError)
        {
            status.Error = restoreError;
            Notify();
            return;
        }

        var job = runner.Start(RemoveJob(serviceDirectory), L($"Deshacer {removed}", $"Undo {removed}"), serviceDirectory, "dotnet", ServiceCommands.RemoveMigration(definition));
        await job.Completion.ConfigureAwait(false);

        if (job.Status == JobStatus.Succeeded && !MigrationReviewer.List(serviceDirectory, definition).Contains(removed))
        {
            if (status.Review is { } review && review.Name == removed)
            {
                MigrationReviewer.RestoreRenames(serviceDirectory, review.Renames);
            }

            status.Review = null;
            status.Sql = null;
            status.Check = ModelCheck.Pending;
        }

        Notify();
    }

    private void Notify() => Changed?.Invoke();

    private static string Tail(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', lines.TakeLast(15));
    }
}
