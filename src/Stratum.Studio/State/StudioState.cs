using Stratum.Core;
using Stratum.Core.Generation;
using Stratum.Core.Model;
using Stratum.Core.Templating;

using static Stratum.Studio.Localization.I18n;

namespace Stratum.Studio.State;

public sealed class StudioOptions
{
    /// <summary>Folder that contains the services (the "system").</summary>
    public required string Workspace { get; init; }

    /// <summary>Service folder to open on start, relative to <see cref="Workspace"/>.</summary>
    public string? OpenService { get; init; }

    /// <summary>Local folder with packed Stratum.* packages, passed to new services (see "stratum new --package-source").</summary>
    public string? PackageSource { get; init; }
}

public sealed record WorkspaceService(string Directory, ServiceDefinition Definition);

/// <summary>
/// State of one Studio session (one browser tab). Everything is computed with Stratum.Core:
/// the preview and the review screen show exactly what the CLI would generate.
/// </summary>
public sealed class StudioState(StudioOptions options) : IDisposable
{
    public event Action? Changed;

    /// <summary>A stratum.json changed outside the Studio (CLI, MCP, editor): raised off the UI thread, debounced.</summary>
    public event Action? ExternalChange;

    private FileSystemWatcher? watcher;
    private Timer? debounce;
    private DateTimeOffset lastOwnWrite = DateTimeOffset.MinValue;
    private bool opening;

    /// <summary>The open draft has edits not applied yet (an external change then asks before reloading).</summary>
    public bool Dirty { get; private set; }

    /// <summary>The open service changed on disk while it had edits: the layout offers to reload it.</summary>
    public bool ExternalChangePending { get; private set; }

    public string Workspace { get; } = Path.GetFullPath(options.Workspace);

    /// <summary>The team's templates (.stratum folder), looked up from the workspace upwards.</summary>
    public TeamTemplates? Team { get; private set; } = TeamTemplates.Find(options.Workspace);

    public IReadOnlyList<WorkspaceService> Services { get; private set; } = [];

    public ServiceDraft? Current { get; private set; }

    public string? SelectedEntity { get; set; }

    /// <summary>Validation problems of the current draft (shown instead of a plan).</summary>
    public IReadOnlyList<string> Errors { get; private set; } = [];

    public ChangePlan? Plan { get; private set; }

    public IReadOnlyList<GeneratedFile> Preview { get; private set; } = [];

    public string? LastMessage { get; set; }

    public void Initialize()
    {
        if (watcher is null && Directory.Exists(Workspace))
        {
            try
            {
                watcher = new FileSystemWatcher(Workspace, ServiceGenerator.DefinitionFileName)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                watcher.Changed += OnDiskChange;
                watcher.Created += OnDiskChange;
                watcher.Deleted += OnDiskChange;
                watcher.Renamed += OnDiskChange;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException)
            {
                watcher = null;   // no live reload; the rest works
            }
        }

        if (Services.Count == 0)
        {
            Reload();
            if (options.OpenService is { } open && Services.FirstOrDefault(s => s.Directory == open) is { } service)
            {
                Open(service);
            }
        }
    }

    /// <summary>The broker most services of the system use, else Kafka.</summary>
    public string SystemBroker() =>
        EventMapEditor.SystemBroker(Services.Select(s => s.Definition)) ?? MessagingSettings.Kafka;

    /// <summary>
    /// Messaging follows usage: publishing or listening to an event turns it on (with the system's broker) instead of
    /// asking the user to go to another page first.
    /// </summary>
    public void EnsureBroker(ServiceDraft draft, string reason)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Broker is null)
        {
            draft.Broker = SystemBroker();
            draft.BrokerNote = L($"Mensajería activada con {draft.Broker} para {reason}. Puedes cambiar el broker en Mensajería.", $"Messaging turned on with {draft.Broker} to {reason}. You can change the broker in Messaging.");
        }
    }

    private void OnDiskChange(object sender, FileSystemEventArgs e)
    {
        if (DateTimeOffset.UtcNow - lastOwnWrite < TimeSpan.FromSeconds(3))
        {
            return;   // our own Apply
        }

        debounce?.Dispose();
        debounce = new Timer(_ => ExternalChange?.Invoke(), null, TimeSpan.FromMilliseconds(600), Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Call on the UI thread after <see cref="ExternalChange"/>: refreshes the services and the open service (right
    /// away if it has no edits; otherwise it waits for <see cref="ReloadCurrent"/>).
    /// </summary>
    public void HandleExternalChange()
    {
        Reload();
        if (Current is { IsNew: false } open)
        {
            var refreshed = Services.FirstOrDefault(x => FullPath(x.Directory) == FullPath(open.Directory));
            if (refreshed is null)
            {
                Current = null;
            }
            else if (Dirty)
            {
                ExternalChangePending = true;
            }
            else
            {
                Open(refreshed);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Discards the edits of the open service and loads what is on disk.</summary>
    public void ReloadCurrent()
    {
        Reload();
        ExternalChangePending = false;
        if (Current is { } open && Services.FirstOrDefault(x => FullPath(x.Directory) == FullPath(open.Directory)) is { } refreshed)
        {
            Open(refreshed);
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        watcher?.Dispose();
        debounce?.Dispose();
    }

    public void Reload()
    {
        Services = [.. Discover()];
        Team = TeamTemplates.Find(Workspace);   // picks up templates edited while the Studio is open
    }

    /// <summary>Services whose Stratum.* packages aren't at this Stratum's version, with the versions they use.</summary>
    public IReadOnlyList<(WorkspaceService Service, IReadOnlyList<string> Versions)> PackagesBehind() =>
        [.. Services.Select(s => (Service: s, Versions: ServiceUpgrader.PackageVersions(FullPath(s.Directory))))
            .Where(x => x.Versions.Count > 0 && x.Versions.Any(v => v != StratumInfo.Version))];

    /// <summary>
    /// Brings the Stratum.* packages of these services to this version (same as "stratum upgrade"): only
    /// Directory.Packages.props and stratumVersion change; the code doesn't.
    /// </summary>
    public IReadOnlyList<string> UpgradePackages(IEnumerable<WorkspaceService> services)
    {
        var lines = new List<string>();
        lastOwnWrite = DateTimeOffset.UtcNow;
        foreach (var service in services)
        {
            try
            {
                var result = ServiceUpgrader.Upgrade(FullPath(service.Directory), StratumInfo.Version);
                lines.Add($"✔ {service.Definition.Service.Name}: Stratum {result.FromVersion} → {result.ToVersion} ({result.Packages.Count} {L("paquete(s)", "package(s)")}" +
                          (result.Regenerated > 0 ? L($", {result.Regenerated} archivo(s) generados de nuevo)", $", {result.Regenerated} generated file(s) rewritten)") : ")"));
                lines.AddRange(result.Hints.Select(h => "ℹ " + h));
            }
            catch (Exception e) when (e is StratumException or IOException or UnauthorizedAccessException)
            {
                lines.Add($"✘ {service.Definition.Service.Name}: {e.Message}");
            }
        }

        Reload();
        if (!Dirty && Current is { IsNew: false } current && Services.FirstOrDefault(s => s.Directory == current.Directory) is { } reopened)
        {
            Open(reopened);
        }

        Changed?.Invoke();
        return lines;
    }

    /// <summary>A new entity as stratum.json keeps it: 1–N and "restrict" are the defaults, so they aren't written.</summary>
    private static EntityDefinition Normalized(EntityDefinition entity) => entity with
    {
        Properties = [.. entity.Properties.Select(p => p.References is null ? p : p with
        {
            Unique = p.Unique == true ? true : null,
            OnDelete = p.OnDelete is null or DeleteBehaviors.Restrict ? null : p.OnDelete,
        })],
    };

    public void Open(WorkspaceService service)
    {
        Current = ServiceDraft.ForExisting(service.Directory, service.Definition);
        Current.UsesWiring = ServicePlanner.UsesStratumWiring(Path.Combine(Workspace, service.Directory), service.Definition);
        SelectedEntity = Current.Entities.FirstOrDefault()?.Name;
        ExternalChangePending = false;
        opening = true;
        try
        {
            Recompute();
        }
        finally
        {
            opening = false;
        }

        Dirty = false;
    }

    /// <param name="location">Folder where the service folder is created. Null: the workspace. Elsewhere, the
    /// service is listed in stratum.workspace.json so the Studio and the AppHost find it.</param>
    public void StartNewService(string name, string? location = null)
    {
        var normalized = ServiceName.Normalize(name);
        var parent = string.IsNullOrWhiteSpace(location) ? Workspace : Path.GetFullPath(Path.Combine(Workspace, location.Trim()));
        if (Directory.Exists(parent) && Directory.EnumerateFiles(parent, "*.csproj").Any())
        {
            throw new StratumException(L($"'{parent}' es la carpeta de un proyecto .NET: elige la carpeta donde quieres el servicio.", $"'{parent}' is the folder of a .NET project: choose the folder where you want the service."));
        }

        for (var folder = new DirectoryInfo(parent); folder is not null; folder = folder.Parent)
        {
            if (ServiceGenerator.FindDefinition(folder.FullName) is not null)
            {
                throw new StratumException(L($"'{folder.FullName}' ya es un servicio: no se crean servicios dentro de otro. Elige una carpeta del sistema que no sea un servicio.", $"'{folder.FullName}' is already a service: services aren't created inside another. Choose a folder of the system that isn't a service."));
            }

            if (string.Equals(folder.FullName.TrimEnd(Path.DirectorySeparatorChar), Workspace.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        var full = Path.Combine(parent, ServiceName.ToKebab(normalized));
        if (Directory.Exists(full))
        {
            throw new StratumException(L($"Ya existe la carpeta '{full}'.", $"The folder '{full}' already exists."));
        }

        // Inside the workspace: relative ("orders"); elsewhere: the full path.
        var directory = WorkspaceFile.IsInside(Workspace, full) ? Path.GetRelativePath(Workspace, full) : full;
        Current = new ServiceDraft { Directory = directory, IsNew = true, Name = normalized };
        SelectedEntity = null;
        Recompute();
    }

    /// <summary>
    /// A new service drafted from an OpenAPI document (file relative to the system, absolute, or URL): its entities
    /// and operations are filled in to review before applying. Returns the import report.
    /// </summary>
    public string StartFromOpenApi(string source, string? architecture, string? location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        source = source.Trim();
        string text;
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            try
            {
                text = http.GetStringAsync(new Uri(source)).GetAwaiter().GetResult();
            }
            catch (HttpRequestException ex)
            {
                throw new StratumException(L($"No se pudo descargar {source}: {ex.Message}", $"Couldn't download {source}: {ex.Message}"), ex);
            }
        }
        else
        {
            var path = Path.GetFullPath(Path.Combine(Workspace, source));
            text = File.Exists(path) ? File.ReadAllText(path) : throw new StratumException(L($"No existe el archivo '{path}'.", $"The file '{path}' doesn't exist."));
        }

        var prepared = Stratum.Core.Import.OpenApiImporter.Prepare(Stratum.Core.Import.OpenApiImporter.Load(text), null, null, architecture);
        StartNewService(prepared.ServiceName, location);
        var draft = Current!;
        draft.Architecture = prepared.Architecture;
        draft.Entities.AddRange(prepared.Entities.Select(e => EntityDraft.From(e, isNew: true)));
        draft.Enums.AddRange(prepared.Enums.Select(e => new EnumDraft { Name = e.Name, Values = string.Join(", ", e.Values) }));
        draft.Types.AddRange(prepared.Types.Select(t => new TypeDraft { Name = t.Name, Properties = PropertyTypes.Spec(t.Properties) }));
        SelectedEntity = prepared.Entities.FirstOrDefault()?.Name;
        Recompute();
        return prepared.Report();
    }

    public EntityDraft AddEntity()
    {
        var draft = Current ?? throw new InvalidOperationException(L("No hay servicio abierto.", "No service is open."));
        var name = "Entity";
        for (var i = 2; draft.Entities.Any(e => e.Name == name); i++)
        {
            name = $"Entity{i}";
        }

        var entity = new EntityDraft { Name = name, IsNew = true };
        entity.Properties.Add(new PropertyDraft { Name = "Name", Type = "string" });
        draft.Entities.Add(entity);
        SelectedEntity = entity.Name;
        Recompute();
        return entity;
    }

    /// <summary>A new entity disappears from the draft; an existing one is marked (and can be restored).</summary>
    public void RemoveEntity(EntityDraft entity)
    {
        if (Current is null)
        {
            return;
        }

        if (entity.IsNew)
        {
            Current.Entities.Remove(entity);
            SelectedEntity = Current.Entities.FirstOrDefault()?.Name;
        }
        else
        {
            entity.Removed = !entity.Removed;
        }

        Recompute();
    }

    /// <summary>Forgets every unapplied edit of the open service.</summary>
    public void Discard()
    {
        if (Current is { IsNew: false } draft && Services.FirstOrDefault(s => s.Directory == draft.Directory) is { } service)
        {
            var selected = SelectedEntity;
            Open(service);
            SelectedEntity = Current?.Entities.Any(e => e.Name == selected) == true ? selected : SelectedEntity;
            Changed?.Invoke();
        }
    }

    /// <summary>Recalculates preview and plan after any edit.</summary>
    public void Recompute()
    {
        if (!opening)
        {
            Dirty = true;
        }

        Plan = null;
        Preview = [];
        var errors = new List<string>();

        if (Current is { } draft)
        {
            try
            {
                var definition = BuildDefinition(draft, errors);
                Preview = ServiceGenerator.Generate(definition, new GenerationOptions { Team = Team });

                if (errors.Count == 0)
                {
                    Plan = draft.IsNew
                        ? ServicePlanner.NewService(Path.Combine(Workspace, draft.Directory), definition, PackageOptions())
                        : PlanForExisting(draft, definition);
                }
            }
            catch (StratumException ex)
            {
                errors.Add(ex.Message);
            }
        }

        Errors = errors;
        Changed?.Invoke();
    }

    public void ApplyPlan()
    {
        if (Plan is null || Current is null)
        {
            return;
        }

        var count = Plan.Changes.Count;
        lastOwnWrite = DateTimeOffset.UtcNow;
        Plan.Apply();
        var full = FullPath(Current.Directory);
        WorkspaceFile.Add(Workspace, full);   // no-op inside the workspace
        Reload();
        LastMessage = L($"✔ {count} archivo(s) escritos en {full}", $"✔ {count} file(s) written in {full}");

        if (Services.FirstOrDefault(s => FullPath(s.Directory) == full) is { } service)
        {
            Open(service);
        }
    }

    /// <summary>
    /// A file of the open service by name ("Order.Ship.cs", "OrderShippedHandler.cs"), wherever Stratum put it; null
    /// when it doesn't exist yet (not applied).
    /// </summary>
    public string? FindServiceFile(string fileName)
    {
        if (Current is not { IsNew: false } open)
        {
            return null;
        }

        var root = FullPath(open.Directory);
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string FullPath(string serviceDirectory) => Path.GetFullPath(Path.Combine(Workspace, serviceDirectory));

    /// <summary>True for services outside the workspace folder (listed in stratum.workspace.json).</summary>
    public bool IsExternal(WorkspaceService service) => !WorkspaceFile.IsInside(Workspace, FullPath(service.Directory));

    /// <summary>Adds an existing Stratum service from another folder to this system.</summary>
    public void AddExternalService(string directory)
    {
        var full = Path.GetFullPath(Path.Combine(Workspace, directory.Trim()));
        if (!WorkspaceFile.Add(Workspace, full) && !Services.Any(s => FullPath(s.Directory) == full))
        {
            throw new StratumException(L($"'{full}' no se ha añadido: ¿tiene src/<Servicio>.Api?", $"'{full}' wasn't added: does it have src/<Service>.Api?"));
        }

        Reload();
        Changed?.Invoke();
    }

    /// <summary>Stops showing an external service (its files stay where they are).</summary>
    public void ForgetService(WorkspaceService service)
    {
        WorkspaceFile.Remove(Workspace, FullPath(service.Directory));
        Reload();
        Changed?.Invoke();
    }

    /// <summary>Events published by the other services of the system, ready to subscribe to.</summary>
    public IReadOnlyList<Subscription> AvailableEvents(ServiceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var own = draft.IsNew ? null : FullPath(draft.Directory);
        return [.. Services
            .Where(s => FullPath(s.Directory) != own && s.Definition.Messaging is not null)
            .SelectMany(s => IntegrationEvents.Of(s.Definition).Select(e => ServicePlanner.SubscriptionTo(s.Definition, e.Entity.Name, e.Name)))
            .OrderBy(s => s.Topic, StringComparer.Ordinal)];
    }

    /// <summary>Entities of the other services a replica can copy: they publish Created, Updated and Deleted.</summary>
    public IReadOnlyList<(ServiceDefinition Publisher, string Entity, IReadOnlyList<PropertyDefinition> Fields)> ReplicaCandidates(ServiceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var own = draft.IsNew ? null : FullPath(draft.Directory);
        return [.. Services
            .Where(s => FullPath(s.Directory) != own && s.Definition.Messaging is not null)
            .SelectMany(s => s.Definition.Entities
                .Where(e => Subscription.Events.All(e.PublishesEvent) && !draft.Replicas.Any(r => r.Entity == e.Name))
                .Select(e => (s.Definition, e.Name, Replicas.Copiable(ServicePlanner.SubscriptionTo(s.Definition, e.Name, Subscription.Created)))))
            .OrderBy(x => x.Definition.Service.Name, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal)];
    }

    /// <summary>Adds a replica to the draft (the change is applied with the rest).</summary>
    public void AddReplica(ServiceDraft draft, ServiceDefinition publisher, string entity, IReadOnlyList<PropertyDefinition> fields)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(publisher);
        draft.Replicas.Add(new ReplicaDefinition { Service = publisher.Service.Name, Entity = entity, Properties = [.. fields] });
        foreach (var name in Subscription.Events)
        {
            var subscription = ServicePlanner.SubscriptionTo(publisher, entity, name) with { Handler = false };
            draft.ReplicaSubscriptions.RemoveAll(s => s.Topic == subscription.Topic);
            draft.ReplicaSubscriptions.Add(subscription);
        }

        EnsureBroker(draft, L("la réplica de ", "the replica of ") + entity);
        Recompute();
    }

    /// <summary>Copies again the contract of the replica's events from the publisher (its fields that still exist stay).</summary>
    public void RefreshReplica(ServiceDraft draft, ReplicaDefinition replica)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(replica);
        foreach (var (subscription, latest, _) in SubscriptionProblems(draft).Where(p => p.Current is not null && draft.ReplicaSubscriptions.Contains(p.Subscription)))
        {
            draft.ReplicaSubscriptions[draft.ReplicaSubscriptions.IndexOf(subscription)] = latest! with { Handler = false };
        }

        var created = draft.ReplicaSubscriptions.First(s => s.Service == replica.Service && s.Entity == replica.Entity && s.Event == Subscription.Created);
        var copiable = Replicas.Copiable(created);
        draft.Replicas[draft.Replicas.IndexOf(replica)] = replica with
        {
            Properties = [.. replica.Properties.Select(p => copiable.FirstOrDefault(c => c.Name == p.Name)).OfType<PropertyDefinition>()],
        };
        Recompute();
    }

    /// <summary>Subscriptions of the draft whose contract changed in the publisher (or that it no longer publishes).</summary>
    public IReadOnlyList<(Subscription Subscription, Subscription? Current, string Problem)> SubscriptionProblems(ServiceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var probe = new ServiceDefinition
        {
            Service = new ServiceInfo { Name = draft.Name },
            Architecture = draft.Architecture,
            Subscriptions = [.. draft.Subscriptions, .. draft.ReplicaSubscriptions],
        };
        return ServicePlanner.CheckSubscriptions(probe, Services.Select(s => s.Definition));
    }

    /// <summary>Everything removing the open service implies (nothing is touched).</summary>
    public ServiceRemovalPlan PlanRemoval(ServiceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return ServiceRemoval.Plan(Workspace, FullPath(draft.Directory));
    }

    /// <summary>Removes the service (see <see cref="ServiceRemoval"/>) and closes it.</summary>
    public IReadOnlyList<string> RemoveService(ServiceRemovalPlan plan, bool deleteFolder)
    {
        var done = ServiceRemoval.Apply(plan, deleteFolder);
        Current = null;
        SelectedEntity = null;
        Plan = null;
        Reload();
        LastMessage = L($"✔ {plan.ServiceName} eliminado. ", $"✔ {plan.ServiceName} deleted. ") + string.Join(" ", done);
        Changed?.Invoke();
        return done;
    }

    /// <summary>Other services subscribed to the events of an entity of the open service.</summary>
    public IReadOnlyList<string> ConsumersOf(ServiceDraft draft, string entity)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return [.. Services
            .Where(s => (s.Definition.Subscriptions ?? []).Any(sub => sub.Service == draft.Name && sub.Entity == entity))
            .Select(s => s.Definition.Service.Name)];
    }

    public IReadOnlyList<EventRoute> EventRoutes() => EventMap.Build(Services.Select(s => s.Definition));

    /// <summary>AsyncAPI of the system's events, as YAML (what <c>stratum events asyncapi</c> writes).</summary>
    public string AsyncApiYaml() =>
        AsyncApiDocument.ToYaml(AsyncApiDocument.Build(Services.Select(s => s.Definition), $"{new DirectoryInfo(Workspace).Name} events"));

    /// <summary>Writes asyncapi.yaml in the system folder and returns its path.</summary>
    public string ExportAsyncApi()
    {
        var path = Path.Combine(Workspace, "asyncapi.yaml");
        File.WriteAllText(path, AsyncApiYaml());
        return path;
    }

    public AspireResult GenerateAppHost(string name)
    {
        var result = AspireGenerator.Generate(Workspace, ServiceName.Normalize(name), [.. AspireGenerator.Discover(Workspace)]);
        Reload();
        Changed?.Invoke();
        return result;
    }

    public string? ExistingAppHost =>
        Directory.EnumerateDirectories(Workspace, "*.AppHost").Select(Path.GetFileName).FirstOrDefault();

    private ChangePlan PlanForExisting(ServiceDraft draft, ServiceDefinition updated)
    {
        var directory = Path.Combine(Workspace, draft.Directory);
        var current = ServicePlanner.Load(directory);
        var options = new GenerationOptions { Team = Team };
        if (!draft.ConvertWiring)
        {
            return ServicePlanner.Plan(directory, current, updated, options, draft.DeleteOwnFiles, Services.Select(s => s.Definition));
        }

        // Program.cs rewritten to use StratumWiring.g.cs, plus whatever else changed (features, entities).
        var conversion = ServicePlanner.ConvertWiring(directory, current, options);
        var plan = ServicePlanner.Plan(directory, current, updated, options, draft.DeleteOwnFiles, Services.Select(s => s.Definition));
        var converted = conversion.Changes.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        return plan with
        {
            Changes = [.. conversion.Changes.Select(c => plan.Changes.FirstOrDefault(p => p.Path == c.Path) is { } regenerated && c.Kind != ChangeKind.Replace ? regenerated : c),
                       .. plan.Changes.Where(c => !converted.Contains(c.Path))],
            Warnings = [.. conversion.Warnings, .. plan.Warnings],
        };
    }

    private ServiceDefinition BuildDefinition(ServiceDraft draft, List<string> errors)
    {
        var definition = draft.IsNew
            ? new ServiceDefinition
            {
                Service = new ServiceInfo { Name = ServiceName.Normalize(draft.Name) },
                Architecture = draft.Architecture,
                Features = draft.Features,
                StratumVersion = draft.StratumVersion,
                Messaging = draft.Broker is null ? null : new MessagingSettings { Broker = draft.Broker },
            }
            : ServicePlanner.Load(Path.Combine(Workspace, draft.Directory));

        // Enumerations first: entity properties may use them.
        try
        {
            var enums = draft.ToEnums();
            foreach (var renamed in draft.Enums.Where(e => e.OriginalName is not null && !string.Equals(e.OriginalName, e.Name.Trim(), StringComparison.Ordinal)))
            {
                if (ModelRules.EnumUsages(definition, renamed.OriginalName!) is { Count: > 0 } usages)
                {
                    errors.Add(L($"No se puede renombrar {renamed.OriginalName}: lo usan {string.Join(", ", usages)}.", $"{renamed.OriginalName} can't be renamed: it's used by {string.Join(", ", usages)}."));
                }
            }

            definition = definition with { Enums = enums };
        }
        catch (StratumException ex)
        {
            errors.Add(ex.Message);
        }

        // Item types next: they may use enumerations, and entity lists use them.
        try
        {
            var types = draft.ToTypes(definition.EnumNames);
            foreach (var renamed in draft.Types.Where(t => t.OriginalName is not null && !string.Equals(t.OriginalName, t.Name.Trim(), StringComparison.Ordinal)))
            {
                if (ModelRules.TypeUsages(definition, renamed.OriginalName!) is { Count: > 0 } usages)
                {
                    errors.Add(L($"No se puede renombrar {renamed.OriginalName}: lo usan {string.Join(", ", usages)}.", $"{renamed.OriginalName} can't be renamed: it's used by {string.Join(", ", usages)}."));
                }
            }

            definition = definition with { Types = types };
        }
        catch (StratumException ex)
        {
            errors.Add(ex.Message);
        }

        if (!draft.IsNew && draft.Features != definition.Features)
        {
            definition = draft.ConvertWiring
                ? definition with { Features = draft.Features }
                : ServicePlanner.SetFeatures(Path.Combine(Workspace, draft.Directory), definition, draft.Features);
        }

        if (!draft.IsNew && draft.Broker != definition.Messaging?.Broker)
        {
            definition = draft.ConvertWiring
                ? definition with { Messaging = draft.Broker is null ? null : new MessagingSettings { Broker = draft.Broker } }
                : ServicePlanner.SetMessaging(Path.Combine(Workspace, draft.Directory), definition, draft.Broker);
        }

        if (draft.Broker is null && draft.Entities.Where(e => !e.Removed).SelectMany(e => e.Operations).Any(o => o.Publish))
        {
            errors.Add(L("Hay operaciones marcadas como Publica y el servicio no tiene mensajería: elige un broker en Mensajería o desmárcalas.", "Some operations are marked Publish and the service has no messaging: choose a broker in Messaging or untick them."));
        }

        var all = draft.AllSubscriptions(definition.Subscriptions);
        var subscriptions = all.Count == 0 ? null : all;
        if (!(subscriptions ?? []).SequenceEqual(definition.Subscriptions ?? []))
        {
            if (subscriptions is not null && definition.Messaging is null)
            {
                errors.Add(L("Para escuchar eventos de otros servicios, activa la mensajería (Servicio → Mensajería).", "To listen to other services' events, turn messaging on (Service → Messaging)."));
            }
            else
            {
                definition = definition with { Subscriptions = subscriptions };
            }
        }

        if (!draft.Replicas.SequenceEqual(definition.Replicas ?? []))
        {
            if (draft.Replicas.Count > 0 && definition.Persistence is null && !draft.Entities.Any(e => !e.Removed))
            {
                errors.Add(L("Para guardar una réplica el servicio necesita base de datos: añade antes una entidad.", "To store a replica the service needs a database: add an entity first."));
            }
            else
            {
                definition = definition with { Replicas = draft.Replicas.Count == 0 ? null : [.. draft.Replicas] };
            }
        }

        // Existing entities: convert, remove or update them (same rules as "stratum entity ...").
        foreach (var entity in draft.Entities.Where(e => !e.IsNew))
        {
            try
            {
                if (entity.Removed)
                {
                    definition = ServicePlanner.RemoveEntity(definition, entity.Name);
                    continue;
                }

                if (entity.IsLegacy && !entity.Convert)
                {
                    continue;
                }

                if (entity.IsLegacy)
                {
                    definition = ServicePlanner.ConvertEntity(definition, entity.Name);
                }

                var renames = entity.Properties
                    .Where(p => p.IsRenamed)
                    .Select(p => new PropertyRename { Entity = entity.Name, From = p.OriginalName!, To = p.Name.Trim() })
                    .ToList();
                definition = ServicePlanner.UpdateEntity(definition, entity.Name, entity.ToDefinition().Properties, renames, keepRelationOptions: false);
                definition = ServicePlanner.SetOperations(definition, entity.Name, entity.ToOperations(definition.EnumNames, definition.TypeNames));
                if (entity.PublishEvents != ServicePlanner.FindEntity(definition, entity.Name).Publishes && (definition.Messaging is not null || !entity.PublishEvents))
                {
                    definition = ServicePlanner.SetPublishEvents(definition, entity.Name, entity.PublishEvents);
                }
            }
            catch (StratumException ex)
            {
                errors.Add($"{entity.Name}: {ex.Message}");
            }
        }

        // New entities are validated one by one, against everything before them (same rules as the CLI).
        foreach (var entity in draft.Entities.Where(e => e.IsNew))
        {
            try
            {
                var accepted = ServicePlanner.ValidateEntity(Normalized(entity.ToDefinition()), definition) with { Operations = entity.ToOperations(definition.EnumNames, definition.TypeNames) };
                definition = definition with { Entities = [.. definition.Entities, accepted] };
                if (entity.PublishEvents && definition.Messaging is not null)
                {
                    definition = ServicePlanner.SetPublishEvents(definition, accepted.Name, publish: true);
                }
            }
            catch (StratumException ex)
            {
                errors.Add($"{(string.IsNullOrWhiteSpace(entity.Name) ? L("Entidad sin nombre", "Unnamed entity") : entity.Name)}: {ex.Message}");
            }
        }

        definition = definition with
        {
            Persistence = definition.Persistence
                ?? (definition.Entities.Count > 0 ? new PersistenceSettings { Provider = draft.Provider } : null),
        };

        // References and operations involve several entities: checked on the whole model.
        if (errors.Count == 0)
        {
            try
            {
                ModelRules.Validate(definition);
            }
            catch (StratumException ex)
            {
                errors.Add(ex.Message);
            }
        }

        return definition;
    }

    private GenerationOptions PackageOptions() =>
        new() { LocalPackageSource = options.PackageSource ?? StratumPackages.LocalSource(), Team = Team };

    private IEnumerable<WorkspaceService> Discover()
    {
        foreach (var service in AspireGenerator.Discover(Workspace))
        {
            yield return new WorkspaceService(service.RelativeDirectory, service.Definition);
        }
    }
}
