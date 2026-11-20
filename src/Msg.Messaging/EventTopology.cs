using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Msg.Messaging;

/// <summary>Topic names and JSON of integration events.</summary>
public static partial class EventTopology
{
    private static readonly ConcurrentDictionary<Type, string> Topics = new();

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    public static string TopicOf(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return Topics.GetOrAdd(eventType, static type =>
            type.GetCustomAttribute<IntegrationEventAttribute>()?.Topic
            ?? WordBoundary().Replace(type.Name, "-$1").ToLowerInvariant());
    }

    public static string TopicOf<TEvent>() => TopicOf(typeof(TEvent));

    [GeneratedRegex("(?<=[a-z0-9])([A-Z])")]
    private static partial Regex WordBoundary();
}
