using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Stratum.Core;
using Stratum.Core.Generation;
using Stratum.Core.Model;
using Stratum.Core.Templating;
using Stratum.Studio.State;

namespace Stratum.Studio;

/// <summary>
/// The events designer: a page of its own (plain HTML + JS, opened in a separate window) and the small JSON API it
/// uses. The rules are the Studio's (<see cref="EventDesigner"/>); the page only draws and collects changes.
/// </summary>
internal static class DesignerApi
{
    public sealed record EventDto(string Name, string EventClass, string Topic, bool Published, string? Operation);

    public sealed record EntityDto(string Name, IReadOnlyList<EventDto> Events);

    public sealed record ListenDto(string Topic, string EventClass, string Publisher, string Entity, string Event, string? Problem);

    public sealed record ServiceDto(string Directory, string Name, string Architecture, string? Broker, IReadOnlyList<EntityDto> Entities, IReadOnlyList<ListenDto> Listens);

    public sealed record ModelDto(string System, IReadOnlyList<ServiceDto> Services, Dictionary<string, DesignerLayout.CardPosition>? Layout);

    public sealed record ChangesDto(IReadOnlyList<DesignerChange> Changes, Dictionary<string, DesignerLayout.CardPosition>? Layout);

    public sealed record FileDto(string Path, string Kind);

    public sealed record ServicePlanDto(string Service, IReadOnlyList<string> Notes, IReadOnlyList<string> Warnings, IReadOnlyList<FileDto> Files);

    public sealed record PlanDto(IReadOnlyList<ServicePlanDto> Services, IReadOnlyList<string> Errors, bool Saved);

    public static void Map(WebApplication app, StudioOptions options)
    {
        app.MapGet("/designer", () => Results.Content(Page(), "text/html; charset=utf-8"));
        app.MapGet("/designer/api/model", () => Model(options.Workspace));
        app.MapPost("/designer/api/preview", (ChangesDto body) => Plan(options, body, apply: false));
        app.MapPost("/designer/api/save", (ChangesDto body) => Plan(options, body, apply: true));
    }

    /// <summary>
    /// The planner's "your files of removed entities are kept" warning, when it only lists event handlers: the
    /// designer already says so in its own words ("Se conserva …Handler.cs"), with the right advice.
    /// </summary>
    private static bool OnlyKeptHandlers(string warning)
    {
        var colon = warning.LastIndexOf(": ", StringComparison.Ordinal);
        return colon > 0 && warning.Contains("tuyos", StringComparison.Ordinal)
            && warning[(colon + 2)..].Split(", ").All(f => f.Trim().EndsWith("Handler.cs", StringComparison.Ordinal));
    }

    private static string Page() => Render("Stratum.Studio.Designer.designer.html");

    /// <summary>An embedded designer page, with its version and its texts in the Studio's language.</summary>
    internal static string Render(string resource)
    {
        using var stream = typeof(DesignerApi).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"{resource} is not embedded.");
        using var reader = new StreamReader(stream);
        var english = Localization.I18n.IsEnglish;
        var page = reader.ReadToEnd()
            .Replace("{{version}}", StratumInfo.Version, StringComparison.Ordinal)
            .Replace("{{lang}}", Localization.I18n.Current, StringComparison.Ordinal);

        // Texts in both languages, "{{español::English}}": the Studio's language picks one.
        return System.Text.RegularExpressions.Regex.Replace(page, @"\{\{(.*?)::(.*?)\}\}", m => m.Groups[english ? 2 : 1].Value, System.Text.RegularExpressions.RegexOptions.Singleline);
    }

    private static IReadOnlyList<DesignerService> Services(string workspace) =>
        [.. AspireGenerator.Discover(workspace).Select(s => new DesignerService(Path.GetFullPath(Path.Combine(workspace, s.RelativeDirectory)), s.Definition))];

    private static ModelDto Model(string workspace)
    {
        var services = AspireGenerator.Discover(workspace);
        var problems = EventMap.Build(services.Select(s => s.Definition)).Where(r => r.Problem is not null).ToDictionary(r => r.Topic, r => r.Problem);
        var layout = DesignerLayout.Load(workspace);
        return new ModelDto(
            Path.GetFileName(Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar)),
            [.. services.Select(s =>
            {
                var d = s.Definition;
                var entities = d.Entities.Where(e => e.IsEditable).Select(e =>
                {
                    var published = IntegrationEvents.Of(d, e).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                    return new EntityDto(e.Name, [.. IntegrationEvents.Possible(d, e).Select(p =>
                        new EventDto(p.Name, p.EventClass, p.Topic, published.Contains(p.Name), p.Operation?.Name))]);
                }).ToList();
                var listens = (d.Subscriptions ?? []).Select(x => new ListenDto(x.Topic, x.EventClass, x.Service, x.Entity, x.Event, problems.GetValueOrDefault(x.Topic))).ToList();
                return new ServiceDto(s.RelativeDirectory, d.Service.Name, d.Architecture, d.Messaging?.Broker, entities, listens);
            })],
            layout.Services);
    }

    private static IResult Plan(StudioOptions options, ChangesDto body, bool apply)
    {
        var workspace = options.Workspace;
        var plan = EventDesigner.Plan(Services(workspace), body.Changes ?? [], new GenerationOptions { Team = TeamTemplates.Find(workspace) });
        var saved = false;
        if (apply && plan.Errors.Count == 0)
        {
            foreach (var service in plan.Services)
            {
                service.Plan.Apply();
            }

            if (body.Layout is not null)
            {
                var layout = DesignerLayout.Load(workspace);   // keeps the entities designers' positions
                layout.Services = body.Layout;
                layout.Save(workspace);
            }

            saved = true;
        }

        return Results.Ok(new PlanDto(
            [.. plan.Services.Select(s => new ServicePlanDto(s.Service, s.Notes, [.. s.Plan.Warnings.Where(w => !OnlyKeptHandlers(w))],
                [.. s.Plan.Changes.Select(c => new FileDto(c.Path, c.Kind.ToString()))]))],
            plan.Errors,
            saved));
    }
}
