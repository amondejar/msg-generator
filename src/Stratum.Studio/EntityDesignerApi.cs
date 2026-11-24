using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Stratum.Core;
using Stratum.Core.Generation;
using Stratum.Core.Model;
using Stratum.Core.Templating;
using Stratum.Studio.Localization;
using Stratum.Studio.State;

namespace Stratum.Studio;

/// <summary>
/// The entities designer of a service: a page of its own (opened in a separate window, like the events designer) with
/// the entities, their relations (1–N, 1–1) and compositions on a canvas, and the JSON API it uses.
/// The rules are Stratum.Core's (<see cref="EntityDesigner"/>, <see cref="Relations"/>).
/// </summary>
internal static class EntityDesignerApi
{
    public sealed record PropertyDto(string Name, string Type, string Kind, string? Target, bool List, bool Optional, bool OneToOne, string? OnDelete);

    public sealed record CardDto(string Name, string? Plural, bool IsType, bool Editable, IReadOnlyList<PropertyDto> Properties);

    public sealed record ModelDto(string Service, string Directory, string Architecture, string? Provider, IReadOnlyList<CardDto> Entities,
        IReadOnlyList<CardDto> Types, IReadOnlyList<string> Enums, Dictionary<string, DesignerLayout.CardPosition> Layout, bool HasDocs);

    public sealed record ChangesDto(string Service, IReadOnlyList<EntityChange> Changes, Dictionary<string, DesignerLayout.CardPosition>? Layout);

    public sealed record FileDto(string Path, string Kind);

    public sealed record PlanDto(IReadOnlyList<FileDto> Files, IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors, bool Saved, bool NeedsMigration);

    public static void Map(WebApplication app, StudioOptions options)
    {
        app.MapGet("/entity-designer", () => Results.Content(DesignerApi.Render("Stratum.Studio.Designer.entity-designer.html"), "text/html; charset=utf-8"));
        app.MapGet("/entity-designer/api/model", (string service) => Model(options.Workspace, service));
        app.MapPost("/entity-designer/api/preview", (ChangesDto body) => Plan(options, body, apply: false));
        app.MapPost("/entity-designer/api/save", (ChangesDto body) => Plan(options, body, apply: true));
    }

    private static string FullPath(string workspace, string service) => Path.GetFullPath(Path.Combine(workspace, service));

    private static IResult Model(string workspace, string service)
    {
        var directory = FullPath(workspace, service);
        if (ServiceGenerator.FindDefinition(directory) is null)
        {
            return Results.NotFound(I18n.L($"No hay ningún servicio en {service}.", $"There's no service in {service}."));
        }

        var d = ServicePlanner.Load(directory);
        var layout = DesignerLayout.Load(workspace).Entities.GetValueOrDefault(service) ?? [];
        return Results.Ok(new ModelDto(
            d.Service.Name,
            service,
            d.Architecture,
            d.Persistence?.Provider,
            [.. d.Entities.Select(e => new CardDto(e.Name, e.Plural, false, e.IsEditable, [.. e.Properties.Select(Property)]))],
            [.. (d.Types ?? []).Select(t => new CardDto(t.Name, null, true, true, [.. t.Properties.Select(Property)]))],
            [.. d.EnumNames],
            layout,
            SpecGenerator.HasDocs(directory)));
    }

    private static PropertyDto Property(PropertyDefinition p) => new(
        p.Name,
        PropertyTypes.Spec(p)[(p.Name.Length + 1)..],
        p.References is not null ? "reference" : p.Type == PropertyTypes.ObjectType ? "item" : "plain",
        p.References ?? p.Of,
        p.IsList,
        p.Nullable,
        p.IsOneToOne,
        p.References is null ? null : p.DeleteBehavior);

    private static IResult Plan(StudioOptions options, ChangesDto body, bool apply)
    {
        var workspace = options.Workspace;
        var directory = FullPath(workspace, body.Service);
        var current = ServicePlanner.Load(directory);
        var result = EntityDesigner.Plan(directory, current, body.Changes ?? [], new GenerationOptions { Team = TeamTemplates.Find(workspace) });
        var saved = false;
        if (apply && result.Errors.Count == 0 && result.Plan is { } plan)
        {
            plan.Apply();
            if (body.Layout is not null)
            {
                var layout = DesignerLayout.Load(workspace);
                layout.Entities[body.Service] = body.Layout;
                layout.Save(workspace);
            }

            saved = true;
        }

        var files = result.Plan?.Changes ?? [];
        return Results.Ok(new PlanDto(
            [.. files.Select(c => new FileDto(c.Path, c.Kind.ToString()))],
            result.Plan?.Warnings ?? [],
            result.Errors,
            saved,
            files.Any(c => c.Path.Contains("Configuration.g.cs", StringComparison.Ordinal))));
    }
}
