using System.Text.Json;
using System.Text.Json.Serialization;
using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>
/// One edit made in the events designer. Kind: "connect" (Consumer listens to the event; the publisher publishes it
/// if it didn't), "disconnect", "publish", "unpublish".
/// </summary>
public sealed record DesignerChange(string Kind, string Publisher, string Entity, string Event, string? Consumer = null)
{
    public string Describe() => Kind switch
    {
        "connect" => $"{Consumer} escucha {Entity}{Event} de {Publisher}",
        "disconnect" => $"{Consumer} deja de escuchar {Entity}{Event} de {Publisher}",
        "publish" => $"{Publisher} publica {Entity}{Event}",
        "unpublish" => $"{Publisher} deja de publicar {Entity}{Event}",
        _ => $"{Kind} {Publisher}.{Entity}.{Event}",
    };
}

/// <summary>What saving the designer's changes does to one service.</summary>
public sealed record DesignerServicePlan(string Service, string Directory, ChangePlan Plan, IReadOnlyList<string> Notes);

/// <summary>The plans of every service the changes touch, and the changes that can't be done (with why).</summary>
public sealed record DesignerPlan(IReadOnlyList<DesignerServicePlan> Services, IReadOnlyList<string> Errors);

/// <summary>A service of the system, as the designer works with it.</summary>
public sealed record DesignerService(string Directory, ServiceDefinition Definition);

/// <summary>
/// The events designer: a batch of connections between services, planned (and applied) together. Same rules as
/// "stratum subscribe" and "stratum events": messaging turns on where needed, contracts are copied, handlers created.
/// </summary>
public static class EventDesigner
{
    public static DesignerPlan Plan(IReadOnlyList<DesignerService> services, IReadOnlyList<DesignerChange> changes, GenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(changes);
        var current = services.ToDictionary(s => s.Definition.Service.Name, s => s.Definition, StringComparer.OrdinalIgnoreCase);
        var directories = services.ToDictionary(s => s.Definition.Service.Name, s => s.Directory, StringComparer.OrdinalIgnoreCase);
        var notes = services.ToDictionary(s => s.Definition.Service.Name, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var broker = EventMapEditor.SystemBroker(services.Select(s => s.Definition));

        ServiceDefinition Get(string name) => current.TryGetValue(name, out var d) ? d
            : throw new StratumException($"No hay ningún servicio '{name}' en el sistema.");

        void Publish(string publisher, string entity, string eventName, bool on)
        {
            var definition = Get(publisher);
            var entityDefinition = ServicePlanner.FindEntity(definition, entity);
            if (on && IntegrationEvents.Of(definition, entityDefinition).Any(e => e.Name.Equals(eventName, StringComparison.OrdinalIgnoreCase)))
            {
                return;   // already published
            }

            if (on)
            {
                definition = ServicePlanner.EnsureMessaging(directories[publisher], definition, broker, out var note);
                if (note is not null)
                {
                    notes[publisher].Add(note);
                }
            }

            current[definition.Service.Name] = ServicePlanner.SetPublishedEvent(definition, entity, eventName, on);
        }

        foreach (var change in changes)
        {
            try
            {
                switch (change.Kind)
                {
                    case "publish":
                        Publish(change.Publisher, change.Entity, change.Event, on: true);
                        break;
                    case "unpublish":
                        Publish(change.Publisher, change.Entity, change.Event, on: false);
                        var topic = Subscription.TopicFor(Get(change.Publisher).Service.Name, change.Entity, change.Event);
                        var listeners = current.Values.Where(d => (d.Subscriptions ?? []).Any(x => x.Topic == topic)).Select(d => d.Service.Name).ToList();
                        if (listeners.Count > 0)
                        {
                            notes[change.Publisher].Add($"⚠ Lo siguen escuchando {string.Join(", ", listeners)}: se quedarán esperando un evento que ya no llega.");
                        }

                        break;
                    case "connect":
                    {
                        var consumerName = change.Consumer ?? throw new StratumException("Falta el servicio que escucha.");
                        if (consumerName.Equals(change.Publisher, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new StratumException("Un servicio no escucha sus propios eventos de integración.");
                        }

                        var consumer = Get(consumerName);   // checked before touching the publisher
                        Publish(change.Publisher, change.Entity, change.Event, on: true);
                        var publisher = Get(change.Publisher);
                        var subscription = ServicePlanner.SubscriptionTo(publisher, change.Entity, change.Event);
                        if ((consumer.Subscriptions ?? []).Any(x => x.Topic == subscription.Topic))
                        {
                            break;   // already listening
                        }

                        consumer = ServicePlanner.EnsureMessaging(directories[consumerName], consumer, publisher.Messaging?.Broker, out var note);
                        if (note is not null)
                        {
                            notes[consumerName].Add(note);
                        }
                        else if (publisher.Messaging is not null && consumer.Messaging!.Broker != publisher.Messaging.Broker)
                        {
                            notes[consumerName].Add($"⚠ {consumer.Service.Name} usa {consumer.Messaging.Broker} y {publisher.Service.Name} publica por {publisher.Messaging.Broker}: no le llegará hasta que usen el mismo broker.");
                        }

                        current[consumer.Service.Name] = ServicePlanner.Subscribe(consumer, subscription);
                        notes[consumerName].Add($"Handler {subscription.EventClass}Handler.cs creado: ahí va lo que hace {consumer.Service.Name} con el evento.");
                        break;
                    }

                    case "disconnect":
                    {
                        var consumerName = change.Consumer ?? throw new StratumException("Falta el servicio que escucha.");
                        current[Get(consumerName).Service.Name] = ServicePlanner.Unsubscribe(Get(consumerName), Get(change.Publisher).Service.Name, change.Entity, change.Event);
                        notes[consumerName].Add($"Se conserva {change.Entity}{change.Event}Handler.cs (tuyo): bórralo si ya no lo necesitas.");
                        break;
                    }

                    default:
                        throw new StratumException($"Cambio desconocido: {change.Kind}.");
                }
            }
            catch (StratumException e)
            {
                errors.Add($"{change.Describe()}: {e.Message}");
            }
        }

        var plans = new List<DesignerServicePlan>();
        foreach (var service in services)
        {
            var name = service.Definition.Service.Name;
            var updated = current[name];
            if (Same(service.Definition, updated))
            {
                continue;
            }

            try
            {
                plans.Add(new DesignerServicePlan(name, service.Directory, ServicePlanner.Plan(service.Directory, service.Definition, updated, options), notes[name]));
            }
            catch (StratumException e)
            {
                errors.Add($"{name}: {e.Message}");
            }
        }

        return new DesignerPlan(plans, errors);
    }

    private static bool Same(ServiceDefinition a, ServiceDefinition b) =>
        ReferenceEquals(a, b) || ServiceGenerator.Serialize(a) == ServiceGenerator.Serialize(b);
}

/// <summary>Where each service sits on the designer's canvas (stratum.designer.json in the system folder).</summary>
public sealed class DesignerLayout
{
    public const string FileName = "stratum.designer.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Services on the canvas, by folder (relative to the system): position of their card.</summary>
    public Dictionary<string, CardPosition> Services { get; init; } = [];

    public sealed record CardPosition(double X, double Y);

    public static DesignerLayout Load(string workspace)
    {
        var path = Path.Combine(workspace, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<DesignerLayout>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string workspace) =>
        File.WriteAllText(Path.Combine(workspace, FileName), JsonSerializer.Serialize(this, Json));
}
