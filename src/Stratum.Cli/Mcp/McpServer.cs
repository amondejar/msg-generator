using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stratum.Core;
using Stratum.Core.Generation;
using Stratum.Core.Model;

namespace Stratum.Cli.Mcp;

/// <summary>
/// "stratum mcp": Stratum as a Model Context Protocol server over stdio (newline-delimited JSON-RPC 2.0), so an AI
/// assistant (Claude, Copilot, Cursor...) changes services through Stratum's model instead of editing generated code.
/// Every tool that changes something plans first (the same plan as --dry-run) and only writes with "apply": true,
/// which the assistant should send after the user has seen the plan. Each tool runs the matching CLI command, so the
/// MCP server, the CLI and the Studio behave the same.
/// Dual-era: answers "initialize" (2025-11-25 and earlier) and per-request "_meta" (2026-07-28).
/// </summary>
public sealed class McpServer(string root, string? packageSource)
{
    public const string ModernVersion = "2026-07-28";

    private const string VersionKey = "io.modelcontextprotocol/protocolVersion";

    private static readonly string[] LegacyVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private static readonly string[] SupportedVersions = [ModernVersion, .. LegacyVersions];

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _root = Path.GetFullPath(root);

    public const string Instructions = """
        Stratum generates and maintains .NET microservices (minimal APIs, EF Core, Aspire, messaging) from a model:
        the stratum.json of each service. Change services through these tools, never by editing *.g.cs files (they are
        regenerated and hand edits are reported). Files without .g.cs (partial classes, handlers, Program.cs) belong
        to the user and are safe to edit.
        Workflow: call list_services first. Every tool that changes something returns a plan when "apply" is false
        (the default): show the plan to the user and call the tool again with "apply": true only after they agree.
        After adding or changing entities, the user creates an EF Core migration (the tool result says how) and can
        run review_migration. Services that exchange events must use the same broker; event_map shows problems.
        Property types: string, int, long, decimal, bool, guid, date, datetime; add ? for optional ("Notes:string?").
        A reference to another entity of the same service uses its name as type ("CustomerId:Customer"): a foreign
        key that is checked on create/update and blocks deletes while in use. Across services, keep a guid and
        subscribe to the other service's events instead. Business actions of an aggregate (clean-ddd) are
        operations (add_operation), not properties.
        """;

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var response = Handle(line);
            if (response is not null)
            {
                await output.WriteLineAsync(response.ToJsonString(Json)).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One JSON-RPC message in, the response out (null for notifications).</summary>
    public JsonObject? Handle(string line)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error");
        }

        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();
        if (method is null)
        {
            return id is null ? null : Error(id, -32600, "Invalid request: no method");
        }

        if (id is null)
        {
            return null;   // notifications (initialized, cancelled...) need no answer
        }

        var parameters = request["params"] as JsonObject ?? [];
        if (parameters["_meta"]?[VersionKey]?.GetValue<string>() is { } requested && !SupportedVersions.Contains(requested))
        {
            return Error(id, -32022, "Unsupported protocol version",
                new JsonObject { ["supported"] = Array(SupportedVersions), ["requested"] = requested });
        }

        try
        {
            JsonObject result = method switch
            {
                "initialize" => Initialize(parameters),
                "server/discover" => Discover(),
                "ping" => [],
                "tools/list" => new JsonObject { ["tools"] = McpTools.Definitions() },
                "tools/call" => CallTool(parameters),
                "resources/list" => new JsonObject { ["resources"] = ListResources() },
                "resources/read" => ReadResource(parameters),
                "prompts/list" => new JsonObject { ["prompts"] = McpPrompts.Definitions() },
                "prompts/get" => McpPrompts.Get(parameters),
                _ => throw new McpProtocolException(-32601, $"Method not found: {method}"),
            };
            result["resultType"] = "complete";
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (McpProtocolException ex)
        {
            return Error(id, ex.Code, ex.Message);
        }
#pragma warning disable CA1031 // one failing request must not stop the server the assistant is talking to
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Error(id, -32603, $"Internal error: {ex.Message}");
        }
    }

    private static JsonObject Initialize(JsonObject parameters)
    {
        var requested = parameters["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = requested is not null && LegacyVersions.Contains(requested) ? requested : LegacyVersions[0],
            ["capabilities"] = Capabilities(),
            ["serverInfo"] = ServerInfo(),
            ["instructions"] = Instructions,
        };
    }

    private static JsonObject Discover() => new()
    {
        ["supportedVersions"] = Array(SupportedVersions),
        ["capabilities"] = Capabilities(),
        ["_meta"] = new JsonObject { ["io.modelcontextprotocol/serverInfo"] = ServerInfo() },
        ["instructions"] = Instructions,
    };

    private static JsonObject Capabilities() => new()
    {
        ["tools"] = new JsonObject(),
        ["resources"] = new JsonObject(),
        ["prompts"] = new JsonObject(),
    };

    private static JsonObject ServerInfo() => new()
    {
        ["name"] = "stratum",
        ["title"] = "Stratum",
        ["version"] = StratumInfo.Version,
    };

    private JsonObject CallTool(JsonObject parameters)
    {
        var name = parameters["name"]?.GetValue<string>() ?? throw new McpProtocolException(-32602, "Missing tool name");
        if (!McpTools.Exists(name))
        {
            throw new McpProtocolException(-32602, $"Unknown tool: {name}");
        }

        var arguments = parameters["arguments"] as JsonObject ?? [];
        try
        {
            var outcome = new McpTools(_root, packageSource).Call(name, arguments);
            var result = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = outcome.Text }),
                ["isError"] = false,
            };
            if (outcome.Structured is { } structured)
            {
                result["structuredContent"] = structured;
            }

            return result;
        }
        catch (Exception ex) when (ex is StratumException or ArgumentException or IOException or InvalidOperationException or JsonException or FormatException or KeyNotFoundException)
        {
            // Tool execution errors go back to the model so it can correct the arguments.
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = ex.Message }),
                ["isError"] = true,
            };
        }
    }

    private JsonArray ListResources()
    {
        var resources = new JsonArray();
        foreach (var service in AspireGenerator.Discover(_root))
        {
            resources.Add(new JsonObject
            {
                ["uri"] = $"stratum://services/{service.Name}/stratum.json",
                ["name"] = $"{service.Name}/stratum.json",
                ["title"] = $"Definición de {service.Name}",
                ["description"] = $"Modelo del servicio {service.Name}: entidades, features, mensajería y suscripciones.",
                ["mimeType"] = "application/json",
            });
        }

        resources.Add(new JsonObject
        {
            ["uri"] = "stratum://asyncapi.yaml",
            ["name"] = "asyncapi.yaml",
            ["title"] = "AsyncAPI del sistema",
            ["description"] = "Eventos de integración del sistema (AsyncAPI 3.1).",
            ["mimeType"] = "application/yaml",
        });
        return resources;
    }

    private JsonObject ReadResource(JsonObject parameters)
    {
        var uri = parameters["uri"]?.GetValue<string>() ?? throw new McpProtocolException(-32602, "Missing uri");
        string text, mime;
        if (uri == "stratum://asyncapi.yaml")
        {
            text = AsyncApiDocument.ToYaml(AsyncApiDocument.Build(AspireGenerator.Discover(_root).Select(s => s.Definition), $"{new DirectoryInfo(_root).Name} events"));
            mime = "application/yaml";
        }
        else if (uri.StartsWith("stratum://services/", StringComparison.Ordinal) && uri.EndsWith("/stratum.json", StringComparison.Ordinal))
        {
            var name = uri["stratum://services/".Length..^"/stratum.json".Length];
            var service = AspireGenerator.Discover(_root).FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new McpProtocolException(-32602, $"Resource not found: {uri}");
            text = File.ReadAllText(ServiceGenerator.FindDefinition(Path.GetFullPath(Path.Combine(_root, service.RelativeDirectory)))!);
            mime = "application/json";
        }
        else
        {
            throw new McpProtocolException(-32602, $"Resource not found: {uri}");
        }

        return new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["uri"] = uri, ["mimeType"] = mime, ["text"] = text }),
        };
    }

    private static JsonArray Array(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)v)]);

    private static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null)
        {
            error["data"] = data;
        }

        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = error };
    }
}

internal sealed class McpProtocolException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

internal sealed record ToolOutcome(string Text, JsonNode? Structured = null);
