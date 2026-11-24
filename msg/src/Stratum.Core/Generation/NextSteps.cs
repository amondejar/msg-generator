using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>
/// What to run after changing a service, depending on how it is run: with the Aspire AppHost of its system (which
/// starts the database and applies the migrations on startup) or on its own (docker compose + dotnet ef).
/// </summary>
public static class NextSteps
{
    /// <summary>The AppHost of the system that runs this service, if any (relative to <paramref name="from"/>).</summary>
    public static string? FindAppHost(string serviceDirectory, string serviceName, string? from = null)
    {
        ArgumentNullException.ThrowIfNull(serviceDirectory);
        var directory = new DirectoryInfo(Path.GetFullPath(serviceDirectory)).Parent;
        for (var level = 0; directory is not null && level < 4; level++, directory = directory.Parent)
        {
            IEnumerable<string> candidates;
            try
            {
                candidates = Directory.EnumerateDirectories(directory.FullName, "*.AppHost");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var appHost in candidates.Order(StringComparer.Ordinal))
            {
                var project = Path.Combine(appHost, Path.GetFileName(appHost) + ".csproj");
                if (File.Exists(project) && File.ReadAllText(project).Contains($"{serviceName}.Api.csproj", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetRelativePath(Path.GetFullPath(from ?? Directory.GetCurrentDirectory()), appHost);
                }
            }
        }

        return null;
    }

    /// <summary>"--project src/Orders.Api" (vertical) or the Infrastructure project with the Api as startup (clean-ddd).</summary>
    public static string EfProjects(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var service = definition.Service.Name;
        return definition.Architecture == Architectures.CleanDdd
            ? $"--project src/{service}.Infrastructure --startup-project src/{service}.Api"
            : $"--project src/{service}.Api";
    }

    /// <summary>After a change of the model (entities, properties, lists, messaging): migration, review and run.</summary>
    /// <param name="pathArgument">The --path to repeat in the commands ("." or empty for the current folder).</param>
    public static IReadOnlyList<string> AfterModelChange(string serviceDirectory, ServiceDefinition definition, string migrationName, string? pathArgument = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var path = string.IsNullOrEmpty(pathArgument) || pathArgument == "." ? string.Empty : $" --path {pathArgument}";
        var cd = string.IsNullOrEmpty(path) ? null : $"cd {pathArgument}";
        var lines = new List<string>();
        if (cd is not null)
        {
            lines.Add($"{cd,-46}# las rutas de dotnet ef son relativas al servicio");
        }

        lines.Add($"{"dotnet build",-46}# restaura y compila: dotnet ef lo necesita");
        lines.Add($"dotnet ef migrations add {migrationName} {EfProjects(definition)}");
        lines.Add($"{"stratum migration review",-46}# renombrados sin perder datos, avisos de borrados");
        lines.Add($"{"dotnet test",-46}# los tests usan SQLite en memoria");
        if (FindAppHost(serviceDirectory, definition.Service.Name, from: serviceDirectory) is { } appHost)
        {
            lines.Add($"{$"dotnet run --project {appHost}",-46}# Aspire: base de datos y migraciones al arrancar");
        }
        else
        {
            lines.Add($"{"docker compose up -d",-46}# base de datos local");
            lines.Add($"dotnet ef database update {EfProjects(definition)}");
            lines.Add($"{$"dotnet run --project src/{definition.Service.Name}.Api",-46}# o 'stratum aspire' en la carpeta del sistema");
        }

        return lines;
    }
}
