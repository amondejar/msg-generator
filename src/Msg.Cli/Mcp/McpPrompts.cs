using System.Text.Json.Nodes;

namespace Msg.Cli.Mcp;

/// <summary>Prompts of "stratum mcp": ready-made workflows the user picks in the assistant (e.g. a slash command).</summary>
internal static class McpPrompts
{
    private const string DesignFromSpec = """
        Diseña o amplía el sistema de microservicios a partir de esta especificación funcional, usando las
        herramientas de Stratum:

        1. Llama a list_services y event_map para ver lo que ya existe. Si la especificación es un documento
           OpenAPI, usa import_openapi (primero sin apply) y completa después lo que el informe diga que falta.
        2. Propón, antes de tocar nada:
           - Servicios (uno por contexto de negocio con datos propios; no un servicio por entidad), con arquitectura
             vertical-slice para CRUD sencillo o clean-ddd si hay reglas de negocio.
           - Entidades de cada servicio con sus propiedades (tipos: string, int, long, decimal, bool, guid, date,
             datetime; ? si es opcional). Dentro de un servicio, las relaciones son referencias ("CustomerId:Customer").
             Un servicio guarda el Id de una entidad de otro servicio como guid, no una referencia.
           - En clean-ddd, las acciones de negocio de cada agregado (Confirm, Cancel...) como operaciones.
           - Eventos: qué entidades publican eventos y qué servicios los escuchan (y para qué), con un mismo broker.
           - Lo que la especificación pide y Stratum no genera (reglas de negocio, validaciones complejas,
             integraciones): irá en el código del usuario (partial classes y handlers).
        3. Cuando el usuario lo apruebe, aplícalo con las herramientas: cada una devuelve primero un plan; enséñalo
           y aplica con "apply": true. Orden: create_service, add_entity (primero las entidades a las que apuntan
           otras), add_operation, set_messaging, set_event_publishing, subscribe, generate_apphost.
        4. Termina con los pasos que quedan: migraciones de EF Core por servicio, ejecutar el AppHost y qué código
           tiene que escribir el usuario y dónde.

        Especificación:
        """;

    private const string ReviewSystem = """
        Revisa el sistema de microservicios con las herramientas de Stratum (list_services, event_map, get_asyncapi)
        y dime, por orden de importancia:
        - Problemas del mapa de eventos (contratos desactualizados, brokers distintos, eventos que nadie publica o que
          nadie escucha) y cómo resolverlos (sync_subscriptions, set_messaging...).
        - Entidades anteriores a 0.10 que no se pueden editar, features que faltan (tests, observability) o servicios
          sin autenticación.
        - Servicios que parecen demasiado grandes o demasiado pequeños para su responsabilidad.
        No cambies nada: propón los cambios y espera a que el usuario los apruebe.
        """;

    public static JsonArray Definitions() =>
    [
        new JsonObject
        {
            ["name"] = "design_from_spec",
            ["title"] = "Diseñar servicios desde una especificación",
            ["description"] = "Propone servicios, entidades y eventos a partir de requisitos escritos y los crea con Stratum tras tu aprobación.",
            ["arguments"] = new JsonArray(new JsonObject
            {
                ["name"] = "spec",
                ["description"] = "La especificación: requisitos, historias de usuario o la descripción del dominio.",
                ["required"] = true,
            }),
        },
        new JsonObject
        {
            ["name"] = "review_system",
            ["title"] = "Revisar el sistema",
            ["description"] = "Busca problemas en servicios, eventos y features y propone cómo resolverlos, sin cambiar nada.",
            ["arguments"] = new JsonArray(),
        },
    ];

    public static JsonObject Get(JsonObject parameters)
    {
        var name = parameters["name"]?.GetValue<string>();
        var text = name switch
        {
            "design_from_spec" => DesignFromSpec + "\n" + (parameters["arguments"]?["spec"]?.GetValue<string>() is { Length: > 0 } spec
                ? spec
                : throw new McpProtocolException(-32602, "Missing argument: spec")),
            "review_system" => ReviewSystem,
            _ => throw new McpProtocolException(-32602, $"Unknown prompt: {name}"),
        };

        return new JsonObject
        {
            ["description"] = name,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
            }),
        };
    }
}
