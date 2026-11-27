using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>What removing a service touches in the other services of the system.</summary>
/// <param name="Directory">The other service's folder (full path).</param>
/// <param name="Plan">Its subscriptions to the removed service's events, taken out (contracts and untouched handlers deleted).</param>
public sealed record ConsumerCleanup(string Service, string Directory, IReadOnlyList<Subscription> Subscriptions, ChangePlan Plan);

/// <summary>
/// Everything that removing a service implies, computed before touching anything: its folder, the subscriptions
/// other services have to its events, its entry in stratum.workspace.json and the AppHost. Databases and Kafka topics
/// live in Aspire's containers and are only reported.
/// </summary>
public sealed record ServiceRemovalPlan(
    string Root,
    string ServiceName,
    string Directory,
    bool IsExternal,
    IReadOnlyList<ConsumerCleanup> Consumers,
    string? AppHost,
    IReadOnlyList<string> Notes,
    string? DatabaseProvider = null)
{
    /// <summary>The service's database in the system's server ("customers-db"), when it has one.</summary>
    public string? Database => DatabaseProvider is null ? null : ContainerDatabase.DatabaseName(ServiceName);

    /// <summary>Files in the service folder (what "delete the folder" removes).</summary>
    public int FileCount => System.IO.Directory.Exists(Directory)
        ? System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories).Count()
        : 0;
}

public static class ServiceRemoval
{
    public static ServiceRemovalPlan Plan(string root, string serviceDirectory)
    {
        var rootFull = Path.GetFullPath(root);
        var directory = Path.GetFullPath(Path.Combine(rootFull, serviceDirectory));
        var definition = ServicePlanner.Load(directory);
        var name = definition.Service.Name;

        if (directory == rootFull || WorkspaceFile.IsInside(directory, rootFull))
        {
            throw new StratumException($"'{directory}' contiene el sistema entero: elige la carpeta de un servicio.");
        }

        var consumers = new List<ConsumerCleanup>();
        foreach (var other in AspireGenerator.Discover(rootFull))
        {
            var otherDirectory = Path.GetFullPath(Path.Combine(rootFull, other.RelativeDirectory));
            if (otherDirectory == directory)
            {
                continue;
            }

            var current = other.Definition;
            var affected = (current.Subscriptions ?? []).Where(s => s.Service == name).ToList();
            var replicas = (current.Replicas ?? []).Where(r => r.Service != name).ToList();
            if (affected.Count == 0)
            {
                continue;
            }

            var remaining = (current.Subscriptions ?? []).Where(s => s.Service != name).ToList();
            var updated = current with { Subscriptions = remaining.Count == 0 ? null : remaining, Replicas = replicas.Count == 0 ? null : replicas };
            var plan = ServicePlanner.Plan(otherDirectory, current, updated, new GenerationOptions { Team = Templating.TeamTemplates.Find(otherDirectory) });
            consumers.Add(new ConsumerCleanup(current.Service.Name, otherDirectory, affected, plan));
        }

        var appHost = System.IO.Directory.EnumerateDirectories(rootFull, "*.AppHost").Select(Path.GetFileName).FirstOrDefault();

        var notes = new List<string>();

        if (definition.Messaging?.Broker == MessagingSettings.Kafka && definition.Entities.Any(e => e.Publishes))
        {
            notes.Add($"Los topics de Kafka '{ServiceName.ToKebab(name)}.*' y sus mensajes siguen en el broker; puedes borrarlos desde Kafka UI.");
        }

        if (appHost is not null)
        {
            notes.Add($"{appHost} se regenera sin {name}. Si en tu AppHost.cs usas sus recursos, quita esas líneas.");
        }

        if (System.IO.Directory.Exists(Path.Combine(directory, ".git")))
        {
            notes.Add("La carpeta del servicio es un repositorio Git: al borrarla se pierde también su historial local (lo que no hayas subido).");
        }

        return new ServiceRemovalPlan(rootFull, name, directory, !WorkspaceFile.IsInside(rootFull, directory), consumers, appHost, notes,
            definition.Persistence?.Provider);
    }

    /// <summary>
    /// Applies the plan. Without <paramref name="deleteFolder"/> the service only leaves the system (its files stay).
    /// With <paramref name="dropDatabase"/>, its database is deleted from the server's container at the end (the server
    /// and the other databases stay); if it can't be, the rest is done anyway and the result says how to do it by hand.
    /// </summary>
    /// <returns>What was done, one line per step.</returns>
    public static IReadOnlyList<string> Apply(ServiceRemovalPlan plan, bool deleteFolder, bool dropDatabase = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!deleteFolder && !plan.IsExternal)
        {
            throw new StratumException(
                $"{plan.ServiceName} está dentro de la carpeta del sistema: Stratum lo seguiría encontrando. " +
                "Bórralo, o muévelo fuera de la carpeta antes de quitarlo.");
        }

        var done = new List<string>();

        // First, before changing anything: move the folder aside. Windows refuses to move a folder while a file in it
        // is open (Visual Studio, the running system...), so nothing is left half done; and on any later failure the
        // folder is put back.
        string? moved = null;
        if (deleteFolder && System.IO.Directory.Exists(plan.Directory))
        {
            var parent = System.IO.Path.GetDirectoryName(plan.Directory)!;
            moved = System.IO.Path.Combine(parent, $".{System.IO.Path.GetFileName(plan.Directory)}.removing-{Guid.NewGuid():N}");
            try
            {
                System.IO.Directory.Move(plan.Directory, moved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new StratumException(
                    $"No se ha cambiado nada: hay archivos de {plan.ServiceName} en uso ({ex.Message}). " +
                    "Cierra Visual Studio (o el editor que tenga abierta la carpeta) y para el sistema si está en marcha, y vuelve a intentarlo.", ex);
            }
        }

        try
        {
            ApplyRest(plan, done);
        }
        catch
        {
            if (moved is not null && !System.IO.Directory.Exists(plan.Directory))
            {
                System.IO.Directory.Move(moved, plan.Directory);   // undo: the service is back where it was
            }

            throw;
        }

        if (moved is not null)
        {
            try
            {
                DeleteFolder(moved);
                done.Add($"Carpeta {plan.Directory} borrada.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                done.Add($"⚠ El servicio ya está quitado, pero no se pudo borrar del todo {moved} ({ex.Message}): bórrala a mano.");
            }
        }

        if (plan.Database is { } database)
        {
            if (dropDatabase)
            {
                var (ok, message) = ContainerDatabase.Drop(ContainerDatabase.Locate(plan.DatabaseProvider!, database, plan.AppHost));
                done.Add(ok ? message : "⚠ " + message);
            }
            else
            {
                done.Add($"La base de datos {database} sigue en el servidor de {plan.DatabaseProvider}. Para borrarla: {ContainerDatabase.DropSql(plan.DatabaseProvider!, database)}");
            }
        }

        return done;
    }

    private static void ApplyRest(ServiceRemovalPlan plan, List<string> done)
    {
        foreach (var consumer in plan.Consumers)
        {
            consumer.Plan.Apply();
            done.Add($"{consumer.Service} deja de escuchar {string.Join(", ", consumer.Subscriptions.Select(s => s.EventClass))}.");
        }

        if (WorkspaceFile.Remove(plan.Root, plan.Directory))
        {
            done.Add("Quitado de stratum.workspace.json.");
        }

        if (plan.AppHost is { } appHost)
        {
            var services = AspireGenerator.Discover(plan.Root)
                .Where(s => Path.GetFullPath(Path.Combine(plan.Root, s.RelativeDirectory)) != plan.Directory)
                .ToList();
            if (services.Count > 0)
            {
                AspireGenerator.Generate(plan.Root, appHost[..^".AppHost".Length], services);
                done.Add($"{appHost} actualizado sin {plan.ServiceName}.");
            }
            else
            {
                done.Add($"{appHost} se queda sin servicios: bórralo si ya no lo usas.");
            }
        }

    }

    /// <summary>Deletes the folder, read-only files included (git marks its objects read-only on Windows).</summary>
    private static void DeleteFolder(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        System.IO.Directory.Delete(directory, recursive: true);
    }
}
