using System.Diagnostics;
using System.Text.Json;

namespace Msg.Messaging;

/// <summary><see cref="IEventBus"/> that stores the event in the outbox of the current unit of work.</summary>
internal sealed class OutboxEventBus(IOutboxWriter writer) : IEventBus
{
    public ValueTask PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : class, IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        writer.Add(OutboxMessages.From(integrationEvent, integrationEvent.GetType()));
        return ValueTask.CompletedTask;
    }
}

internal static class OutboxMessages
{
    public static OutboxMessage From(IIntegrationEvent integrationEvent, Type type) => new()
    {
        Id = integrationEvent.EventId,
        Topic = EventTopology.TopicOf(type),
        Type = type.FullName ?? type.Name,
        Payload = JsonSerializer.Serialize(integrationEvent, type, EventTopology.JsonOptions),
        OccurredAt = integrationEvent.OccurredAt.UtcDateTime,
        CorrelationId = Activity.Current?.TraceId.ToString(),
        TraceParent = Activity.Current?.Id,
    };
}
