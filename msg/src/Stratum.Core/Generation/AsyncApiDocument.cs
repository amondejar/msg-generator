using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>
/// AsyncAPI 3.1 description of the integration events of a system: one channel per topic, a "send" operation for the
/// publishing service and a "receive" operation per consumer, and the payload schema of each event as it travels on
/// the wire (System.Text.Json web defaults: camelCase). Built from the stratum.json files, like the event map.
/// </summary>
public static partial class AsyncApiDocument
{
    public const string Version = "3.1.0";

    public static JsonObject Build(IEnumerable<ServiceDefinition> services, string title)
    {
        ArgumentNullException.ThrowIfNull(services);
        var all = services.ToList();
        var routes = EventMap.Build(all);

        var payloads = new Dictionary<string, (string EventClass, IReadOnlyList<PropertyDefinition> Properties, bool Deleted)>(StringComparer.Ordinal);
        var enumValues = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var service in all.Where(s => s.Messaging is not null))
        {
            foreach (var published in IntegrationEvents.Of(service))
            {
                var entity = published.Entity;
                payloads[published.Topic] = (published.EventClass, IntegrationEvents.Payload(service, published), published.IsDeleted);
                foreach (var p in entity.Properties.Concat(published.Operation?.Parameters ?? []).Where(p => p.Enum is not null))
                {
                    if ((service.Enums ?? []).FirstOrDefault(e => e.Name == p.Enum) is { } e)
                    {
                        enumValues[$"{published.Topic}.{p.Name}"] = e.Values;
                    }
                }
            }
        }

        // Events published outside this system: the consumer's copy of the contract is all there is.
        foreach (var subscription in all.SelectMany(s => s.Subscriptions ?? []))
        {
            payloads.TryAdd(subscription.Topic, (subscription.EventClass, subscription.Properties, subscription.Event == Subscription.Deleted));
        }

        var brokerOf = all.Where(s => s.Messaging is not null).ToDictionary(s => s.Service.Name, s => s.Messaging!.Broker, StringComparer.Ordinal);
        var servers = new JsonObject();
        foreach (var broker in brokerOf.Values.Distinct().Order(StringComparer.Ordinal))
        {
            if (Server(broker) is { } server)
            {
                servers[broker] = server;
            }
        }

        var channels = new JsonObject();
        var operations = new JsonObject();
        var messages = new JsonObject();
        var schemas = new JsonObject();
        var tags = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var route in routes)
        {
            var channelId = Identifier(route.Topic);
            var messageId = channelId;
            var (eventClass, properties, deleted) = payloads.TryGetValue(route.Topic, out var p) ? p : (route.EventClass, [], false);

            schemas[messageId] = Schema(eventClass, properties, deleted, property =>
                enumValues.TryGetValue($"{route.Topic}.{property.Name}", out var values) ? values : null);
            messages[messageId] = new JsonObject
            {
                ["name"] = eventClass,
                ["title"] = eventClass,
                ["summary"] = Summary(route, eventClass),
                ["contentType"] = "application/json",
                ["headers"] = new JsonObject { ["$ref"] = "#/components/schemas/StratumHeaders" },
                ["payload"] = new JsonObject { ["$ref"] = $"#/components/schemas/{messageId}" },
            };

            var channel = new JsonObject
            {
                ["address"] = route.Topic,
                ["title"] = eventClass,
                ["messages"] = new JsonObject { [eventClass] = new JsonObject { ["$ref"] = $"#/components/messages/{messageId}" } },
            };
            var channelServers = new[] { route.Publisher }.Concat(route.Consumers)
                .Where(s => s is not null && brokerOf.ContainsKey(s) && servers.ContainsKey(brokerOf[s]))
                .Select(s => brokerOf[s!]).Distinct().Order(StringComparer.Ordinal).ToList();
            if (channelServers.Count > 0)
            {
                channel["servers"] = new JsonArray([.. channelServers.Select(b => (JsonNode)new JsonObject { ["$ref"] = $"#/servers/{b}" })]);
            }

            if (route.Problem is { } problem)
            {
                channel["description"] = "⚠ " + problem;
            }

            channels[channelId] = channel;

            if (route.Publisher is { } publisher)
            {
                tags.Add(publisher);
                operations[Identifier($"{publisher} publish {eventClass}")] = Operation("send", publisher, channelId, eventClass,
                    $"{publisher} publica {eventClass} (outbox, en la misma transacción que el cambio).");
            }

            foreach (var consumer in route.Consumers)
            {
                tags.Add(consumer);
                operations[Identifier($"{consumer} on {route.Topic}")] = Operation("receive", consumer, channelId, eventClass,
                    $"{consumer} procesa {eventClass} una vez por evento (inbox), con reintentos y dead-letter.");
            }
        }

        schemas["StratumHeaders"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Cabeceras que añade Stratum.Messaging (Kafka headers / Service Bus application properties).",
            ["properties"] = new JsonObject
            {
                ["msg-source"] = new JsonObject { ["type"] = "string", ["description"] = "Servicio que lo publicó." },
                ["msg-occurred-at"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
                ["traceparent"] = new JsonObject { ["type"] = "string", ["description"] = "W3C trace context: la traza continúa en el consumidor." },
            },
        };

        var document = new JsonObject
        {
            ["asyncapi"] = Version,
            ["info"] = new JsonObject
            {
                ["title"] = title,
                ["version"] = "1.0.0",
                ["description"] = "Eventos de integración entre los servicios del sistema. Generado por Stratum desde los stratum.json: no lo edites, vuelve a exportarlo.",
                ["tags"] = new JsonArray([.. tags.Select(t => (JsonNode)new JsonObject { ["name"] = t })]),
            },
            ["defaultContentType"] = "application/json",
        };
        if (servers.Count > 0)
        {
            document["servers"] = servers;
        }

        document["channels"] = channels;
        document["operations"] = operations;
        document["components"] = new JsonObject { ["messages"] = messages, ["schemas"] = schemas };
        return document;
    }

    public static string ToJson(JsonObject document) =>
        document.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";

    /// <summary>YAML (block style) of a JSON tree. Strings are quoted when YAML could read them as something else.</summary>
    public static string ToYaml(JsonObject document)
    {
        var builder = new StringBuilder();
        WriteObject(builder, document, 0);
        return builder.ToString();
    }

    private static JsonObject Operation(string action, string service, string channelId, string eventClass, string summary) => new()
    {
        ["action"] = action,
        ["channel"] = new JsonObject { ["$ref"] = $"#/channels/{channelId}" },
        ["summary"] = summary,
        ["tags"] = new JsonArray(new JsonObject { ["name"] = service }),
        ["messages"] = new JsonArray(new JsonObject { ["$ref"] = $"#/channels/{channelId}/messages/{eventClass}" }),
    };

    private static string Summary(EventRoute route, string eventClass) =>
        $"{eventClass}, publicado por {route.Publisher ?? "un servicio externo"}" +
        (route.Consumers.Count == 0 ? "; nadie lo escucha todavía." : $"; lo escuchan {string.Join(", ", route.Consumers)}.");

    private static JsonObject? Server(string broker) => broker switch
    {
        MessagingSettings.Kafka => new JsonObject
        {
            ["host"] = "localhost:9092",
            ["protocol"] = "kafka",
            ["description"] = "Kafka (en local, el contenedor de Aspire). Un consumer group por servicio; fallos a <topic>.dead-letter.",
        },
        MessagingSettings.AzureServiceBus => new JsonObject
        {
            ["host"] = "localhost",
            ["protocol"] = "amqp",
            ["description"] = "Azure Service Bus (en local, el emulador de Aspire). Un topic por evento y una suscripción por servicio; fallos al dead-letter de la suscripción.",
        },
        _ => null,
    };

    private static JsonObject Schema(string eventClass, IReadOnlyList<PropertyDefinition> properties, bool deleted, Func<PropertyDefinition, IReadOnlyList<string>?> enumValues)
    {
        var props = new JsonObject
        {
            ["eventId"] = new JsonObject { ["type"] = "string", ["format"] = "uuid", ["description"] = "Único por evento: los consumidores deduplican por él." },
            ["occurredAt"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
            ["id"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
        };
        var required = new List<string> { "eventId", "occurredAt", "id" };
        if (!deleted)
        {
            foreach (var property in properties)
            {
                var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                props[name] = Type(property);
                if (enumValues(property) is { } values)
                {
                    var target = property.IsList ? props[name]!["items"]! : props[name]!;
                    target["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v), .. property.Nullable ? new JsonNode?[] { null } : []]);
                }
                if (!property.Nullable)
                {
                    required.Add(name);
                }
            }
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["title"] = eventClass,
            ["properties"] = props,
            ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]),
        };
    }

    private static JsonObject Type(PropertyDefinition property)
    {
        if (property.IsList)
        {
            JsonObject items;
            if (property.IsObjectList)
            {
                var fields = new JsonObject();
                foreach (var field in property.Fields ?? [])
                {
                    fields[JsonNamingPolicy.CamelCase.ConvertName(field.Name)] = Type(field);
                }

                items = new JsonObject
                {
                    ["type"] = "object",
                    ["title"] = property.Of,
                    ["properties"] = fields,
                    ["required"] = new JsonArray([.. (property.Fields ?? []).Where(f => !f.Nullable).Select(f => (JsonNode)JsonNamingPolicy.CamelCase.ConvertName(f.Name))]),
                };
            }
            else
            {
                items = Type(property with { List = null });
            }

            return new JsonObject { ["type"] = "array", ["items"] = items };
        }

        var (type, format) = property.Type.ToLowerInvariant() switch
        {
            "int" => ("integer", "int32"),
            "long" => ("integer", "int64"),
            "decimal" => ("number", "decimal"),
            "bool" => ("boolean", null),
            "guid" => ("string", "uuid"),
            "date" => ("string", "date"),
            "datetime" => ("string", "date-time"),
            _ => ("string", (string?)null),
        };
        var schema = new JsonObject
        {
            ["type"] = property.Nullable ? new JsonArray(type, "null") : JsonValue.Create(type),
        };
        if (format is not null)
        {
            schema["format"] = format;
        }

        return schema;
    }

    /// <summary>"orders.order-created" → "ordersOrderCreated" (AsyncAPI ids must be usable in $ref and code generators).</summary>
    private static string Identifier(string text)
    {
        var words = NonWord().Split(text).Where(w => w.Length > 0).ToList();
        var builder = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            builder.Append(i == 0 ? char.ToLowerInvariant(word[0]) : char.ToUpperInvariant(word[0])).Append(word.AsSpan(1));
        }

        return builder.ToString();
    }

    private static void WriteObject(StringBuilder builder, JsonObject node, int indent)
    {
        foreach (var (key, value) in node)
        {
            builder.Append(' ', indent).Append(Scalar(key)).Append(':');
            WriteValue(builder, value, indent);
        }
    }

    private static void WriteValue(StringBuilder builder, JsonNode? value, int indent)
    {
        switch (value)
        {
            case JsonObject { Count: 0 }:
                builder.Append(" {}\n");
                break;
            case JsonObject child:
                builder.Append('\n');
                WriteObject(builder, child, indent + 2);
                break;
            case JsonArray { Count: 0 }:
                builder.Append(" []\n");
                break;
            case JsonArray array:
                builder.Append('\n');
                foreach (var item in array)
                {
                    builder.Append(' ', indent + 2).Append('-');
                    if (item is JsonObject itemObject && itemObject.Count > 0)
                    {
                        // First key on the dash line, the rest aligned under it.
                        var first = true;
                        foreach (var (key, inner) in itemObject)
                        {
                            builder.Append(first ? " " : new string(' ', indent + 4)).Append(Scalar(key)).Append(':');
                            WriteValue(builder, inner, indent + 4);
                            first = false;
                        }
                    }
                    else
                    {
                        WriteValue(builder, item, indent + 2);
                    }
                }

                break;
            default:
                builder.Append(' ').Append(value is null ? "null" : Scalar(value)).Append('\n');
                break;
        }
    }

    private static string Scalar(JsonNode value) =>
        value.GetValueKind() == JsonValueKind.String ? Scalar(value.GetValue<string>()) : value.ToJsonString();

    private static string Scalar(string text) =>
        PlainScalar().IsMatch(text) && !Reserved.Contains(text) && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? text
            : JsonSerializer.Serialize(text, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "yes", "no", "on", "off", "null", "y", "n", "~",
    };

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_./-]*$")]
    private static partial Regex PlainScalar();

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex NonWord();
}
