using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Stratum.Studio.State;

public enum JobStatus
{
    Running,
    Succeeded,
    Failed,
    Stopped,
}

/// <summary>A process launched from the Studio (the Aspire AppHost, "dotnet ef"...), with its live output.</summary>
public sealed class ProcessJob
{
    private const int MaxLines = 500;
    private readonly List<string> _output = [];
    private readonly Lock _lock = new();

    public required string Key { get; init; }

    public required string Title { get; init; }

    public required string CommandLine { get; init; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public JobStatus Status { get; internal set; } = JobStatus.Running;

    public int? ExitCode { get; internal set; }

    /// <summary>First URL matched by the job's detector (e.g. the Aspire dashboard login link).</summary>
    public string? DetectedUrl { get; internal set; }

    internal Process? Process { get; set; }

    internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the process ends (whatever the result).</summary>
    public Task Completion => Done.Task;

    public IReadOnlyList<string> Output
    {
        get
        {
            lock (_lock)
            {
                return [.. _output];
            }
        }
    }

    internal void Append(string line)
    {
        lock (_lock)
        {
            _output.Add(line);
            if (_output.Count > MaxLines)
            {
                _output.RemoveRange(0, _output.Count - MaxLines);
            }
        }
    }
}

/// <summary>
/// Runs and tracks external processes for the whole Studio (singleton): jobs survive page reloads,
/// and every process tree is killed when the Studio stops.
/// </summary>
public sealed partial class ProcessRunner : IDisposable
{
    private readonly ConcurrentDictionary<string, ProcessJob> _jobs = new();

    public event Action? Changed;

    public ProcessJob? Get(string key) => _jobs.TryGetValue(key, out var job) ? job : null;

    public bool IsRunning(string key) => Get(key)?.Status == JobStatus.Running;

    public ProcessJob Start(string key, string title, string workingDirectory, string fileName, IReadOnlyList<string> arguments, Regex? urlDetector = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (IsRunning(key))
        {
            throw new InvalidOperationException($"'{title}' ya se está ejecutando.");
        }

        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var job = new ProcessJob
        {
            Key = key,
            Title = title,
            CommandLine = $"{fileName} {string.Join(' ', arguments.Select(Quote))}",
        };
        job.Append($"> {job.CommandLine}");

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        void OnLine(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            var line = StripAnsi(e.Data);
            job.Append(line);
            if (job.DetectedUrl is null && urlDetector?.Match(line) is { Success: true } match)
            {
                job.DetectedUrl = match.Value;
            }

            Changed?.Invoke();
        }

        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.Exited += (_, _) =>
        {
            process.WaitForExit(); // flushes the redirected output before the job is marked as finished
            job.ExitCode = SafeExitCode(process);
            if (job.Status == JobStatus.Running)
            {
                job.Status = job.ExitCode == 0 ? JobStatus.Succeeded : JobStatus.Failed;
            }

            job.Append(job.Status == JobStatus.Stopped ? "■ Detenido." : $"■ Terminó con código {job.ExitCode}.");
            job.Done.TrySetResult();
            Changed?.Invoke();
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.Append($"No se pudo arrancar '{fileName}': {ex.Message}");
            job.Done.TrySetResult();
            _jobs[key] = job;
            Changed?.Invoke();
            return job;
        }

        job.Process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _jobs[key] = job;
        Changed?.Invoke();
        return job;
    }

    public void Stop(string key)
    {
        if (Get(key) is { Status: JobStatus.Running, Process: { } process } job)
        {
            job.Status = JobStatus.Stopped;
            try
            {
                process.Kill(entireProcessTree: true);   // the AppHost starts the services and containers' proxies
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            Changed?.Invoke();
        }
    }

    /// <summary>Runs a short command to completion (tool checks). Never throws: failures come back as exit code -1.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start)!;
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(20));
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return (process.ExitCode, (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }

    public void Dispose()
    {
        foreach (var key in _jobs.Keys)
        {
            Stop(key);
        }
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();

    private static string StripAnsi(string line) => Ansi().Replace(line, string.Empty);

    private static string Quote(string argument) => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }
}
