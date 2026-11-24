using Stratum.Core.Model;

namespace Stratum.Core.Generation;

public sealed record AddEntityRequest
{
    public required string ServiceDirectory { get; init; }

    public required string Name { get; init; }

    public required string Properties { get; init; }

    public string? Plural { get; init; }

    /// <summary>Only used for the first entity (it decides the service's database).</summary>
    public string? Provider { get; init; }

    /// <summary>Plans without writing anything.</summary>
    public bool DryRun { get; init; }
}

public sealed record AddEntityResult(
    EntityDefinition Entity,
    string Provider,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Warnings,
    string? TeamTemplates = null);

/// <summary>"stratum add entity": plans with <see cref="ServicePlanner"/> and applies the plan.</summary>
public static class EntityAdder
{
    public static AddEntityResult Add(AddEntityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var definition = ServicePlanner.Load(request.ServiceDirectory);
        var entity = ServicePlanner.ValidateEntity(
            new EntityDefinition
            {
                Name = request.Name,
                Plural = request.Plural ?? string.Empty,
                Properties = PropertyTypes.Parse(request.Properties, definition.EnumNames, definition.TypeNames),
            },
            definition);
        var provider = ServicePlanner.ResolveProvider(request.Provider, definition.Persistence);

        var team = Templating.TeamTemplates.Find(request.ServiceDirectory);
        var plan = ServicePlanner.AddEntities(request.ServiceDirectory, definition, [entity], provider, new GenerationOptions { Team = team });
        if (!request.DryRun)
        {
            plan.Apply();
        }

        return new AddEntityResult(
            entity,
            provider,
            [.. plan.Changes.Where(c => c.Kind == ChangeKind.Create).Select(c => c.Path)],
            [.. plan.Changes.Where(c => c.Kind != ChangeKind.Create).Select(c => c.Path)],
            plan.Warnings,
            team?.Root);
    }
}
