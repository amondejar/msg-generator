using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Stratum.Studio.State;

/// <summary>A compiler error or warning, with where it is (to open it in the editor).</summary>
public sealed record BuildIssue(string Code, string Message, string? File, int? Line, bool IsError);

/// <summary>A test that failed and why (first line of its message).</summary>
public sealed record FailedTest(string Name, string Message);

/// <summary>What a build or a test run of a service said, summarized from its output.</summary>
public sealed partial class BuildReport
{
    private readonly Lock gate = new();
    private readonly List<BuildIssue> issues = [];
    private readonly List<FailedTest> failures = [];
    private string? pendingTest;

    public required string Kind { get; init; }

    public int? Passed { get; private set; }

    public int? Failed { get; private set; }

    public int? Skipped { get; private set; }

    public IReadOnlyList<BuildIssue> Errors { get { lock (gate) { return [.. issues.Where(i => i.IsError)]; } } }

    public IReadOnlyList<BuildIssue> Warnings { get { lock (gate) { return [.. issues.Where(i => !i.IsError)]; } } }

    public IReadOnlyList<FailedTest> Failures { get { lock (gate) { return [.. failures]; } } }

    public void Read(string line)
    {
        lock (gate)
        {
            if (Issue().Match(line) is { Success: true } m)
            {
                var issue = new BuildIssue(m.Groups["code"].Value, m.Groups["message"].Value.Trim(),
                    m.Groups["file"].Value, int.TryParse(m.Groups["line"].Value, out var n) ? n : null, m.Groups["kind"].Value == "error");
                if (!issues.Any(i => i.Code == issue.Code && i.File == issue.File && i.Line == issue.Line && i.Message == issue.Message))
                {
                    issues.Add(issue);
                }

                return;
            }

            if (FailedLine().Match(line) is { Success: true } f)
            {
                pendingTest = f.Groups["name"].Value;
                failures.Add(new FailedTest(pendingTest, string.Empty));
                return;
            }

            if (pendingTest is not null && line.Trim() is { Length: > 0 } text && !text.StartsWith("Error Message", StringComparison.Ordinal)
                && !text.StartsWith("Stack Trace", StringComparison.Ordinal))
            {
                failures[^1] = failures[^1] with { Message = text };
                pendingTest = null;
                return;
            }

            if (Summary().Match(line) is { Success: true } s)
            {
                // One summary line per test project: add them up.
                Failed = (Failed ?? 0) + int.Parse(s.Groups["failed"].Value, System.Globalization.CultureInfo.InvariantCulture);
                Passed = (Passed ?? 0) + int.Parse(s.Groups["passed"].Value, System.Globalization.CultureInfo.InvariantCulture);
                Skipped = (Skipped ?? 0) + int.Parse(s.Groups["skipped"].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    // C:\x\Order.Ship.cs(12,13): error CS0103: The name 'x' does not exist [C:\x\Orders.Domain.csproj]
    [GeneratedRegex(@"^\s*(?<file>[^\s(][^(]*?)(\((?<line>\d+)(,\d+)?\))?\s*:\s*(?<kind>error|warning)\s+(?<code>[A-Z]+\d+)\s*:\s*(?<message>.*?)(\s+\[[^\]]+\])?$")]
    private static partial Regex Issue();

    [GeneratedRegex(@"^\s*Failed\s+(?<name>[\w.<>`,\[\]() ""-]+?)\s+\[[^\]]*\]\s*$")]
    private static partial Regex FailedLine();

    // "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: ..."
    [GeneratedRegex(@"Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+)")]
    private static partial Regex Summary();
}

/// <summary>
/// "dotnet build" and "dotnet test" of a service (or of the whole system), from the Studio: live output in the job,
/// plus a summary (errors with file and line, failed tests) that survives page changes.
/// </summary>
public sealed partial class BuildRunner(ProcessRunner runner)
{
    public const string Build = "build";
    public const string Test = "test";

    private readonly ConcurrentDictionary<string, BuildReport> reports = new();

    public static string JobKey(string kind, string directory) => $"{kind}:{Path.GetFullPath(directory)}";

    public BuildReport? Report(string kind, string directory) => reports.GetValueOrDefault(JobKey(kind, directory));

    public bool IsBusy(string directory) => runner.IsRunning(JobKey(Build, directory)) || runner.IsRunning(JobKey(Test, directory));

    /// <param name="target">Folder to run in (a service, or the system with its .slnx).</param>
    /// <param name="solution">Solution or project to build; null: the one in the folder.</param>
    public ProcessJob Start(string kind, string target, string title, string? solution = null)
    {
        var key = JobKey(kind, target);
        var report = new BuildReport { Kind = kind };
        reports[key] = report;
        IReadOnlyList<string> arguments = kind == Test
            ? [Test, .. solution is null ? Array.Empty<string>() : [solution], "--nologo", "--logger", "console;verbosity=normal"]
            : [Build, .. solution is null ? Array.Empty<string>() : [solution], "--nologo", "-clp:NoSummary"];
        return runner.Start(key, title, target, "dotnet", arguments,
            environment: new Dictionary<string, string> { ["DOTNET_CLI_UI_LANGUAGE"] = "en", ["DOTNET_NOLOGO"] = "1" },
            onLine: report.Read);
    }
}
