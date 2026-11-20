using Msg.Core;
using Msg.Core.Generation;
using Msg.Core.Model;
using Msg.Core.Templating;

namespace Msg.Studio.State;

public sealed class StudioOptions
{
    /// <summary>Folder that contains the services (the "system").</summary>
    public required string Workspace { get; init; }

    /// <summary>Service folder to open on start, relative to <see cref="Workspace"/>.</summary>
    public string? OpenService { get; init; }

    /// <summary>Local folder with packed Msg.* packages, passed to new services (see "msg new --package-source").</summary>
    public string? PackageSource { get; init; }
}

public sealed record WorkspaceService(string Directory, ServiceDefinition Definition);

/// <summary>
/// State of one Studio session (one browser tab). Everything is computed with Msg.Core:
/// the preview and the review screen show exactly what the CLI would generate.
/// </summary>
public sealed class StudioState(StudioOptions options)
{
    public event Action? Changed;

    public string Workspace { get; } = Path.GetFullPath(options.Workspace);

    /// <summary>The team's templates (.msg folder), looked up from the workspace upwards.</summary>
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
        Current.UsesWiring = ServicePlanner.UsesMsgWiring(Path.Combine(Workspace, service.Directory), service.Definition);
        SelectedEntity = Current.Entities.FirstOrDefault()?.Name;
        Recompute();
    }

    public void StartNewService(string name)
    {
        var normalized = ServiceName.Normalize(name);
        var directory = ServiceName.ToKebab(normalized);
        if (Directory.Exists(Path.Combine(Workspace, directory)))
        {
            throw new MsgException($"Ya existe la carpeta '{directory}' en el sistema.");
        }

        Current = new ServiceDraft { Directory = directory, IsNew = true, Name = normalized };
        SelectedEntity = null;
        Recompute();
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
            catch (MsgException ex)
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
        var directory = Current.Directory;
        Reload();
        LastMessage = $"✔ {count} archivo(s) escritos en {Path.Combine(Workspace, directory)}";

        if (Services.FirstOrDefault(s => s.Directory == directory) is { } service)
        {
            Open(service);
        }
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

        // Program.cs rewritten to use MsgWiring.g.cs, plus whatever else changed (features, entities).
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
                MsgVersion = draft.MsgVersion,
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

        // Existing entities: convert, remove or update them (same rules as "msg entity ...").
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
                if (entity.PublishEvents != ServicePlanner.FindEntity(definition, entity.Name).Publishes)
                {
                    definition = ServicePlanner.SetPublishEvents(definition, entity.Name, entity.PublishEvents);
                }
            }
            catch (MsgException ex)
            {
                errors.Add($"{entity.Name}: {ex.Message}");
            }
        }

        // New entities are validated one by one, against everything before them (same rules as the CLI).
        foreach (var entity in draft.Entities.Where(e => e.IsNew))
        {
            try
            {
                var accepted = ServicePlanner.ValidateEntity(entity.ToDefinition(), definition);
                definition = definition with { Entities = [.. definition.Entities, accepted] };
                if (entity.PublishEvents)
                {
                    definition = ServicePlanner.SetPublishEvents(definition, accepted.Name, publish: true);
                }
            }
            catch (MsgException ex)
            {
                errors.Add($"{(string.IsNullOrWhiteSpace(entity.Name) ? "Entidad sin nombre" : entity.Name)}: {ex.Message}");
            }
        }

        return definition with
        {
            Persistence = definition.Persistence
                ?? (definition.Entities.Count > 0 ? new PersistenceSettings { Provider = draft.Provider } : null),
        };
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
