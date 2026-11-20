using Stratum.Core;
using Stratum.Core.Generation;
using Stratum.Core.Model;
using Stratum.Core.Templating;

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
public sealed class StudioState(StudioOptions options)
{
    public event Action? Changed;

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
        if (Services.Count == 0)
        {
            Reload();
            if (options.OpenService is { } open && Services.FirstOrDefault(s => s.Directory == open) is { } service)
            {
                Open(service);
            }
        }
    }

    public void Reload()
    {
        Services = [.. Discover()];
        Team = TeamTemplates.Find(Workspace);   // picks up templates edited while the Studio is open
    }

    public void Open(WorkspaceService service)
    {
        Current = ServiceDraft.ForExisting(service.Directory, service.Definition);
        Current.UsesWiring = ServicePlanner.UsesStratumWiring(Path.Combine(Workspace, service.Directory), service.Definition);
        SelectedEntity = Current.Entities.FirstOrDefault()?.Name;
        Recompute();
    }

    /// <param name="location">Folder where the service folder is created. Null: the workspace. Elsewhere, the
    /// service is listed in stratum.workspace.json so the Studio and the AppHost find it.</param>
    public void StartNewService(string name, string? location = null)
    {
        var normalized = ServiceName.Normalize(name);
        var parent = string.IsNullOrWhiteSpace(location) ? Workspace : Path.GetFullPath(Path.Combine(Workspace, location.Trim()));
        if (Directory.Exists(parent) && Directory.EnumerateFiles(parent, "*.csproj").Any())
        {
            throw new StratumException($"'{parent}' es la carpeta de un proyecto .NET: elige la carpeta donde quieres el servicio.");
        }

        var full = Path.Combine(parent, ServiceName.ToKebab(normalized));
        if (Directory.Exists(full))
        {
            throw new StratumException($"Ya existe la carpeta '{full}'.");
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
                throw new StratumException($"No se pudo descargar {source}: {ex.Message}", ex);
            }
        }
        else
        {
            var path = Path.GetFullPath(Path.Combine(Workspace, source));
            text = File.Exists(path) ? File.ReadAllText(path) : throw new StratumException($"No existe el archivo '{path}'.");
        }

        var prepared = Stratum.Core.Import.OpenApiImporter.Prepare(Stratum.Core.Import.OpenApiImporter.Load(text), null, null, architecture);
        StartNewService(prepared.ServiceName, location);
        var draft = Current!;
        draft.Architecture = prepared.Architecture;
        draft.Entities.AddRange(prepared.Entities.Select(e => EntityDraft.From(e, isNew: true)));
        SelectedEntity = prepared.Entities.FirstOrDefault()?.Name;
        Recompute();
        return prepared.Report();
    }

    public EntityDraft AddEntity()
    {
        var draft = Current ?? throw new InvalidOperationException("No hay servicio abierto.");
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
        Plan.Apply();
        var full = FullPath(Current.Directory);
        WorkspaceFile.Add(Workspace, full);   // no-op inside the workspace
        Reload();
        LastMessage = $"✔ {count} archivo(s) escritos en {full}";

        if (Services.FirstOrDefault(s => FullPath(s.Directory) == full) is { } service)
        {
            Open(service);
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
            throw new StratumException($"'{full}' no se ha añadido: ¿tiene src/<Servicio>.Api?");
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
            .SelectMany(s => s.Definition.Entities
                .Where(e => e.IsEditable && e.Publishes)
                .SelectMany(e => Subscription.Events.Select(name => ServicePlanner.SubscriptionTo(s.Definition, e.Name, name))))
            .OrderBy(s => s.Topic, StringComparer.Ordinal)];
    }

    /// <summary>Subscriptions of the draft whose contract changed in the publisher (or that it no longer publishes).</summary>
    public IReadOnlyList<(Subscription Subscription, Subscription? Current, string Problem)> SubscriptionProblems(ServiceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var probe = new ServiceDefinition
        {
            Service = new ServiceInfo { Name = draft.Name },
            Architecture = draft.Architecture,
            Subscriptions = [.. draft.Subscriptions],
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
        LastMessage = $"✔ {plan.ServiceName} eliminado. " + string.Join(" ", done);
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
            return ServicePlanner.Plan(directory, current, updated, options, draft.DeleteOwnFiles);
        }

        // Program.cs rewritten to use StratumWiring.g.cs, plus whatever else changed (features, entities).
        var conversion = ServicePlanner.ConvertWiring(directory, current, options);
        var plan = ServicePlanner.Plan(directory, current, updated, options, draft.DeleteOwnFiles);
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

        var subscriptions = draft.Subscriptions.Count == 0 ? null : draft.Subscriptions.ToList();
        if (!(subscriptions ?? []).SequenceEqual(definition.Subscriptions ?? []))
        {
            if (subscriptions is not null && definition.Messaging is null)
            {
                errors.Add("Para escuchar eventos de otros servicios, activa la mensajería (Servicio → Mensajería).");
            }
            else
            {
                definition = definition with { Subscriptions = subscriptions };
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
                definition = ServicePlanner.UpdateEntity(definition, entity.Name, entity.ToDefinition().Properties, renames);
                definition = ServicePlanner.SetOperations(definition, entity.Name, entity.ToOperations());
                if (entity.PublishEvents != ServicePlanner.FindEntity(definition, entity.Name).Publishes)
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
                var accepted = ServicePlanner.ValidateEntity(entity.ToDefinition(), definition) with { Operations = entity.ToOperations() };
                definition = definition with { Entities = [.. definition.Entities, accepted] };
                if (entity.PublishEvents)
                {
                    definition = ServicePlanner.SetPublishEvents(definition, accepted.Name, publish: true);
                }
            }
            catch (StratumException ex)
            {
                errors.Add($"{(string.IsNullOrWhiteSpace(entity.Name) ? "Entidad sin nombre" : entity.Name)}: {ex.Message}");
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
        new() { LocalPackageSource = options.PackageSource, Team = Team };

    private IEnumerable<WorkspaceService> Discover()
    {
        foreach (var service in AspireGenerator.Discover(Workspace))
        {
            yield return new WorkspaceService(service.RelativeDirectory, service.Definition);
        }
    }
}
