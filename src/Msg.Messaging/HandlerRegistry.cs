using System.Reflection;

namespace Msg.Messaging;

/// <summary>Which handler handles which integration event, and on which topic.</summary>
public sealed class HandlerRegistry
{
    private static readonly MethodInfo InvokeMethod =
        typeof(HandlerRegistry).GetMethod(nameof(Invoke), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly List<HandlerRegistration> _handlers = [];

    public IReadOnlyCollection<string> Topics => [.. _handlers.Select(h => h.Topic).Distinct(StringComparer.Ordinal)];

    public IReadOnlyList<HandlerRegistration> Handlers => _handlers;

    public IReadOnlyList<HandlerRegistration> For(string topic) => [.. _handlers.Where(h => h.Topic == topic)];

    internal bool Add(Type eventType, Type handlerType)
    {
        if (_handlers.Any(h => h.EventType == eventType && h.HandlerType == handlerType))
        {
            return false;
        }

        var invoker = InvokeMethod.MakeGenericMethod(eventType)
            .CreateDelegate<Func<object, object, MessageContext, CancellationToken, Task>>();
        _handlers.Add(new HandlerRegistration(eventType, handlerType, EventTopology.TopicOf(eventType), invoker));
        return true;
    }

    private static Task Invoke<TEvent>(object handler, object integrationEvent, MessageContext context, CancellationToken cancellationToken)
        where TEvent : class, IIntegrationEvent =>
        ((IIntegrationEventHandler<TEvent>)handler).HandleAsync((TEvent)integrationEvent, context, cancellationToken);
}

/// <param name="Invoker">Calls HandleAsync without reflection on every message.</param>
public sealed record HandlerRegistration(
    Type EventType,
    Type HandlerType,
    string Topic,
    Func<object, object, MessageContext, CancellationToken, Task> Invoker)
{
    /// <summary>Name stored in the inbox: renaming the handler class makes old messages look new.</summary>
    public string Consumer => HandlerType.FullName ?? HandlerType.Name;
}
