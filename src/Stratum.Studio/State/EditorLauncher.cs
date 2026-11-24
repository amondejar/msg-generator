using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

using static Stratum.Studio.Localization.I18n;

namespace Stratum.Studio.State;

/// <summary>An editor the Studio can open solutions and files in.</summary>
public sealed record Editor(string Id, string Name, string? Executable);

/// <summary>
/// Opens services and files in the developer's editor (Visual Studio, VS Code, Rider, or the system's default app),
/// so the code to write (rules, handlers) is one click away. The choice is remembered per user.
/// </summary>
public sealed class EditorLauncher
{
    public const string VisualStudio = "vs";
    public const string VsCode = "code";
    public const string Rider = "rider";
    public const string SystemDefault = "system";

    private IReadOnlyList<Editor>? available;

    /// <summary>Editors found on this machine (the system default is always there).</summary>
    public IReadOnlyList<Editor> Available =>
        [.. (available ??= Detect()).Where(e => e.Id != SystemDefault), new Editor(SystemDefault, L("Aplicación predeterminada", "Default app"), null)];

    /// <summary>The chosen editor, or the first one found (Visual Studio, VS Code, Rider, system).</summary>
    public Editor Current
    {
        get
        {
            var saved = Read();
            return Available.FirstOrDefault(e => e.Id == saved) ?? Available[0];
        }
    }

    public void Choose(string id) => StudioSettings.Set("editor", id);

    /// <summary>
    /// The solution to open for a service: the system's (AppHost, every service in one place) when it includes the
    /// service, else the service's own .slnx.
    /// </summary>
    public static string? SolutionFor(string workspace, string serviceDirectory)
    {
        var service = Path.GetFullPath(serviceDirectory);
        foreach (var solution in SafeFiles(workspace, "*.slnx"))
        {
            var text = File.ReadAllText(solution);
            var relative = Path.GetRelativePath(Path.GetDirectoryName(solution)!, service).Replace('\\', '/');
            if (text.Replace('\\', '/').Contains(relative + "/", StringComparison.OrdinalIgnoreCase))
            {
                return solution;
            }
        }

        return SafeFiles(service, "*.slnx").Concat(SafeFiles(service, "*.sln")).FirstOrDefault();
    }

    /// <summary>The system's solution (generated with the AppHost), if any.</summary>
    public static string? SystemSolution(string workspace) => SafeFiles(workspace, "*.slnx").FirstOrDefault();

    /// <summary>Opens a solution (or folder): Visual Studio and Rider open the .slnx, VS Code its folder.</summary>
    public string Open(string solutionOrFolder)
    {
        var editor = Current;
        var folder = Directory.Exists(solutionOrFolder) ? solutionOrFolder : Path.GetDirectoryName(solutionOrFolder)!;
        return editor.Id switch
        {
            VsCode => Launch(editor, [folder]),
            VisualStudio or Rider => Launch(editor, [solutionOrFolder]),
            _ => Shell(solutionOrFolder),
        };
    }

    /// <summary>Opens a file, at a line when known (in Visual Studio, in the instance already open if there is one).</summary>
    public string OpenFile(string file, int? line = null)
    {
        if (!File.Exists(file))
        {
            throw new FileNotFoundException(L($"No existe {file}: aplica antes los cambios para que Stratum lo cree.", $"{file} doesn't exist: apply the changes first so Stratum creates it."), file);
        }

        var editor = Current;
        return editor.Id switch
        {
            VsCode => Launch(editor, ["-r", "-g", line is { } l ? $"{file}:{l}" : file]),
            VisualStudio => Launch(editor, ["/edit", file]),
            Rider => Launch(editor, line is { } l ? ["--line", l.ToString(System.Globalization.CultureInfo.InvariantCulture), file] : [file]),
            _ => Shell(file),
        };
    }

    private static string Launch(Editor editor, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(editor.Executable!) { UseShellExecute = false, CreateNoWindow = true };
        if (editor.Executable!.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // "code" is a .cmd on Windows: run it through cmd.
            start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(editor.Executable);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var _ = Process.Start(start);
        return L($"Abierto en {editor.Name}.", $"Opened in {editor.Name}.");
    }

    private static string Shell(string path)
    {
        using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return L("Abierto con la aplicación predeterminada.", "Opened with the default app.");
    }

    private static string? Read() => StudioSettings.Get("editor");

    private static List<Editor> Detect()
    {
        var found = new List<Editor>();
        if (VisualStudioPath() is { } devenv)
        {
            found.Add(new Editor(VisualStudio, "Visual Studio", devenv));
        }

        if (OnPath(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ["code.cmd", "code.exe"] : ["code"]) is { } code)
        {
            found.Add(new Editor(VsCode, "VS Code", code));
        }

        if (OnPath(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ["rider64.exe", "rider.cmd", "rider.bat"] : ["rider"]) is { } rider)
        {
            found.Add(new Editor(Rider, "Rider", rider));
        }

        found.Add(new Editor(SystemDefault, "Aplicación predeterminada", null));
        return found;
    }

    /// <summary>devenv.exe of the newest Visual Studio, found with vswhere (installed with every VS).</summary>
    private static string? VisualStudioPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(vswhere, "-latest -prerelease -property productPath")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            var path = process?.StandardOutput.ReadToEnd().Trim();
            process?.WaitForExit(5000);
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    private static string? OnPath(IEnumerable<string> names)
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return names.SelectMany(n => folders.Select(f => Path.Combine(f, n))).FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> SafeFiles(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, pattern).Order(StringComparer.Ordinal).ToList() : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
