using System.Diagnostics;
using System.Text.RegularExpressions;
using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>The container of a database server that the AppHost runs (postgres-xxxx, sqlserver-xxxx).</summary>
/// <param name="Runtime">docker or podman.</param>
public sealed record DatabaseContainer(string Runtime, string Id, string Name, bool Running);

/// <summary>Where a service's database lives, or why it couldn't be found.</summary>
public sealed record DatabaseLocation(string Provider, string Database, DatabaseContainer? Container, string? Problem)
{
    /// <summary>The SQL that deletes it, for running it by hand (another server, or no container runtime).</summary>
    public string ManualSql => ContainerDatabase.DropSql(Provider, Database);
}

/// <summary>
/// Deletes a service's database from the container of its server (Docker or Podman), the one Aspire runs for the
/// system: "DROP DATABASE" through the client inside the container (psql or sqlcmd), so no password is needed here.
/// The server and its volume stay: the other services keep their databases.
/// </summary>
public static class ContainerDatabase
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The database Aspire creates for a service ("customers-db").</summary>
    public static string DatabaseName(string service) => ServiceName.ToKebab(service) + "-db";

    /// <summary>Finds the container of the server of <paramref name="provider"/> (preferring the one of this AppHost).</summary>
    public static DatabaseLocation Locate(string provider, string database, string? appHost)
    {
        var runtime = Runtime();
        if (runtime is null)
        {
            return new(provider, database, null, "No se encuentra Docker ni Podman: bórrala a mano en tu servidor.");
        }

        var server = provider == PersistenceSettings.SqlServer ? "sqlserver" : "postgres";
        var image = provider == PersistenceSettings.SqlServer ? "mssql" : "postgres";
        var listed = Run(runtime, ["ps", "-a", "--no-trunc", "--format", "{{.ID}}\t{{.Names}}\t{{.Image}}\t{{.State}}\t{{.Mounts}}"]);
        if (listed.ExitCode != 0)
        {
            return new(provider, database, null, $"{runtime} no responde ({FirstLine(listed.Output)}): ¿está arrancado?");
        }

        var candidates = listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(c => c.Length >= 4
                        && c[1].TrimStart('/').StartsWith(server + "-", StringComparison.OrdinalIgnoreCase)
                        && c[2].Contains(image, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Several systems on one machine: the volume of Aspire's server carries the AppHost's name.
        if (candidates.Count > 1 && appHost is not null)
        {
            var mine = candidates.Where(c => c.Length > 4 && c[4].Contains(appHost, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count > 0)
            {
                candidates = mine;
            }
        }

        return candidates.Count switch
        {
            0 => new(provider, database, null,
                $"No hay ningún contenedor de {server} en {runtime}: si la base de datos está en otro servidor, bórrala allí."),
            1 => new(provider, database, new DatabaseContainer(runtime, candidates[0][0], candidates[0][1].TrimStart('/'),
                candidates[0][3].Equals("running", StringComparison.OrdinalIgnoreCase)), null),
            _ => new(provider, database, null,
                $"Hay varios contenedores de {server} ({string.Join(", ", candidates.Select(c => c[1]))}) y no sé cuál es el de este sistema: bórrala a mano en el suyo."),
        };
    }

    /// <summary>
    /// Deletes the database (a stopped container is started for it, and stopped again). Never throws: the result says
    /// what happened.
    /// </summary>
    public static (bool Ok, string Message) Drop(DatabaseLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.Container is not { } container)
        {
            return (false, $"La base de datos {location.Database} no se ha borrado: {location.Problem} SQL: {location.ManualSql}");
        }

        if (!Regex.IsMatch(location.Database, "^[A-Za-z0-9_-]+$"))
        {
            return (false, $"Nombre de base de datos inesperado: {location.Database}.");
        }

        var started = false;
        if (!container.Running)
        {
            var start = Run(container.Runtime, ["start", container.Id]);
            if (start.ExitCode != 0)
            {
                return (false, $"No se pudo arrancar {container.Name} ({FirstLine(start.Output)}). SQL: {location.ManualSql}");
            }

            started = true;
        }

        try
        {
            // Right after starting, the server needs a few seconds before it accepts connections.
            var deadline = DateTime.UtcNow + (started ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(5));
            (int ExitCode, string Output) result;
            do
            {
                result = Run(container.Runtime, ["exec", container.Id, .. DropCommand(location.Provider, location.Database)]);
                if (result.ExitCode == 0 || DateTime.UtcNow > deadline)
                {
                    break;
                }

                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
            while (true);

            return result.ExitCode == 0
                ? (true, $"Base de datos {location.Database} borrada del contenedor {container.Name} ({container.Runtime}).")
                : (false, $"No se pudo borrar {location.Database} en {container.Name}: {FirstLine(result.Output)}. SQL: {location.ManualSql}");
        }
        finally
        {
            if (started)
            {
                Run(container.Runtime, ["stop", container.Id]);   // as it was
            }
        }
    }

    public static string DropSql(string provider, string database) => provider == PersistenceSettings.SqlServer
        ? $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];"
        : $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE);";

    /// <summary>
    /// The client inside the server's container, with the password of the container itself (its environment): psql
    /// for PostgreSQL, sqlcmd for SQL Server. The SQL goes as an argument, never through the shell.
    /// </summary>
    internal static IReadOnlyList<string> DropCommand(string provider, string database) => provider == PersistenceSettings.SqlServer
        ?
        [
            "sh", "-c",
            "t=/opt/mssql-tools18/bin/sqlcmd; [ -x \"$t\" ] || t=/opt/mssql-tools/bin/sqlcmd; SQLCMDPASSWORD=\"$MSSQL_SA_PASSWORD\" exec \"$t\" \"$@\"",
            "sh", "-C", "-S", "localhost", "-U", "sa", "-b", "-Q",
            $"IF DB_ID(N'{database}') IS NOT NULL BEGIN {DropSql(provider, database)} END",
        ]
        :
        [
            "sh", "-c",
            "PGPASSWORD=\"$POSTGRES_PASSWORD\" exec psql -U \"${POSTGRES_USER:-postgres}\" -d postgres -v ON_ERROR_STOP=1 \"$@\"",
            "sh", "-c", DropSql(provider, database),
        ];

    /// <summary>The container runtime Aspire uses: DOTNET_ASPIRE_CONTAINER_RUNTIME, else docker, else podman.</summary>
    private static string? Runtime()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_ASPIRE_CONTAINER_RUNTIME");
        foreach (var candidate in new[] { configured, "docker", "podman" }.OfType<string>().Where(c => c.Length > 0).Distinct())
        {
            if (Run(candidate, ["version", "--format", "{{.Client.Version}}"]).ExitCode == 0 || Run(candidate, ["--version"]).ExitCode == 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static (int ExitCode, string Output) Run(string file, IReadOnlyList<string> arguments)
    {
        try
        {
            var info = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(CommandTimeout))
            {
                process.Kill(entireProcessTree: true);
                return (-1, "tiempo agotado");
            }

            return (process.ExitCode, stdout.Result + stderr.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "sin detalles";
}
