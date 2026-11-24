using Stratum.Core.Generation;
using Stratum.Core.Model;

namespace Stratum.Studio.State;

/// <summary>
/// Positions of the event map: one column per service, its entities with their events (what it publishes) and the
/// events it listens to. Computed here, not by the browser, so the arrows always meet the boxes.
/// </summary>
public sealed class EventMapLayout
{
    public const int Width = 270;
    public const int Gap = 120;
    public const int Header = 64;
    public const int SectionTitle = 30;
    public const int EntityRow = 40;
    public const int EventRow = 28;
    public const int Padding = 14;

    public sealed record Chip(string Topic, string EventClass, string Entity, string Event, int Y);

    public sealed record EntityRow2(EntityDefinition Entity, bool Publishes, int Y, IReadOnlyList<Chip> Events);

    public sealed record Listen(Subscription Subscription, int Y, string? Problem);

    public sealed record Column(WorkspaceService Service, int X, int Height, IReadOnlyList<EntityRow2> Entities, int ListenTitleY, IReadOnlyList<Listen> Listens);

    public sealed record Arrow(string Topic, string Path, bool Problem, string Publisher, string Consumer);

    public IReadOnlyList<Column> Columns { get; }

    public IReadOnlyList<Arrow> Arrows { get; }

    public int TotalWidth { get; }

    public int TotalHeight { get; }

    public EventMapLayout(IReadOnlyList<WorkspaceService> services, IReadOnlyList<EventRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(routes);
        var problems = routes.Where(r => r.Problem is not null).ToDictionary(r => r.Topic, r => r.Problem, StringComparer.Ordinal);

        // Publishers first (left), then services that only listen: arrows mostly flow left to right.
        var ordered = services
            .OrderByDescending(s => IntegrationEvents.Of(s.Definition).Count > 0)
            .ThenBy(s => s.Definition.Service.Name, StringComparer.Ordinal)
            .ToList();

        var columns = new List<Column>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var service = ordered[i];
            var d = service.Definition;
            var y = Header + SectionTitle;
            var entities = new List<EntityRow2>();
            foreach (var entity in d.Entities.Where(e => e.IsEditable))
            {
                var rowY = y;
                y += EntityRow;
                var chips = new List<Chip>();
                foreach (var published in IntegrationEvents.Of(d, entity))
                {
                    chips.Add(new Chip(published.Topic, published.EventClass, entity.Name, published.Name, y));
                    y += EventRow;
                }

                entities.Add(new EntityRow2(entity, entity.Publishes && d.Messaging is not null, rowY, chips));
            }

            if (entities.Count == 0)
            {
                y += EntityRow;   // "no entities" line
            }

            var listenTitle = y + 6;
            y = listenTitle + SectionTitle;
            var listens = new List<Listen>();
            foreach (var subscription in d.Subscriptions ?? [])
            {
                listens.Add(new Listen(subscription, y, problems.GetValueOrDefault(subscription.Topic)));
                y += EventRow;
            }

            if (listens.Count == 0)
            {
                y += EventRow;   // "listens to nothing" line
            }

            columns.Add(new Column(service, i * (Width + Gap), y + Padding, entities, listenTitle, listens));
        }

        var arrows = new List<Arrow>();
        foreach (var consumer in columns)
        {
            foreach (var listen in consumer.Listens)
            {
                var publisher = columns.FirstOrDefault(c => c.Service.Definition.Service.Name == listen.Subscription.Service);
                var chip = publisher?.Entities.SelectMany(e => e.Events).FirstOrDefault(c => c.Topic == listen.Subscription.Topic);
                if (publisher is null || chip is null)
                {
                    continue;   // nobody publishes it: the row itself is marked
                }

                var fromRight = consumer.X > publisher.X;
                var x1 = fromRight ? publisher.X + Width : publisher.X;
                var y1 = chip.Y + (EventRow / 2);
                var x2 = fromRight ? consumer.X : consumer.X + Width;
                var y2 = listen.Y + (EventRow / 2);
                var bend = Math.Max(60, Math.Abs(x2 - x1) / 2);
                var c1 = fromRight ? x1 + bend : x1 - bend;
                var c2 = fromRight ? x2 - bend : x2 + bend;
                arrows.Add(new Arrow(listen.Subscription.Topic, $"M {x1} {y1} C {c1} {y1}, {c2} {y2}, {x2} {y2}",
                    listen.Problem is not null, publisher.Service.Definition.Service.Name, consumer.Service.Definition.Service.Name));
            }
        }

        Columns = columns;
        Arrows = arrows;
        TotalWidth = columns.Count == 0 ? 0 : columns[^1].X + Width;
        TotalHeight = columns.Count == 0 ? 0 : columns.Max(c => c.Height);
    }
}
