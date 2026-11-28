using System.Text;
using System.Text.Json.Nodes;
using Stratum.Core;
using Stratum.Core.Generation;
using Stratum.Core.Model;

namespace Stratum.Cli.Mcp;

/// <summary>The tools of "stratum mcp". Each one maps to a CLI command; tools that change files plan unless "apply" is true.</summary>
internal sealed class McpTools(string root, string? packageSource)
{
    private sealed record Tool(string Name, string Title, string Description, JsonObject Properties, string[] Required, bool ReadOnly, bool Destructive = false);

    private const string PlanNote = "Sin \"apply\": true solo devuelve el plan (no escribe nada). Enséñaselo al usuario y repite con \"apply\": true cuando lo apruebe.";

    private static readonly Tool[] All =
    [
        new("list_services", "Servicios del sistema",
            "Lista los servicios Stratum del sistema (esta carpeta y stratum.workspace.json): arquitectura, base de datos, broker, features, entidades con sus propiedades, eventos que publican y que escuchan. Llama a esta primero.",
            [], [], ReadOnly: true),
        new("get_service", "Definición de un servicio",
            "Devuelve el stratum.json de un servicio: la fuente de verdad de lo que Stratum genera.",
            new JsonObject { ["service"] = Service() }, ["service"], ReadOnly: true),
        new("event_map", "Mapa de eventos",
            "Quién publica y quién escucha cada evento de integración, con avisos (contratos desactualizados, brokers distintos, eventos que nadie publica).",
            [], [], ReadOnly: true),
        new("get_specification", "Especificación e historias de usuario",
            "Especificación funcional en Markdown (en español) generada del modelo: entidades con sus reglas, API con respuestas, ejemplos, operaciones, eventos que publica y escucha, y dónde va cada cosa. Sin \"service\", la del sistema (servicios y eventos entre ellos, con diagrama). Con \"story\" (p. ej. \"ORD-06\") devuelve esa historia de usuario: criterios de aceptación en Gherkin y reglas por completar. Úsala antes de implementar reglas o handlers.",
            new JsonObject
            {
                ["service"] = Service("Servicio: su nombre o su carpeta. Vacío: el sistema."),
                ["story"] = Text("Id de una historia (\"ORD-06\") para leerla entera."),
            }, [], ReadOnly: true),
        new("generate_docs", "Generar la documentación",
            "Crea o actualiza docs/ del servicio (o de todos y docs/sistema.g.md si no se indica servicio): especificacion.g.md (de Stratum), contexto.md e historias de usuario (del usuario: se crean una vez, nunca se sobrescriben). Desde entonces cada cambio del modelo mantiene la especificación al día. " + PlanNote,
            new JsonObject { ["service"] = Service("Servicio: su nombre o su carpeta. Vacío: todo el sistema."), ["apply"] = Apply() },
            [], ReadOnly: false),
        new("get_asyncapi", "AsyncAPI del sistema",
            "Especificación AsyncAPI 3.1 (YAML) de los eventos del sistema. Con \"save\": true la guarda en asyncapi.yaml en la carpeta del sistema.",
            new JsonObject { ["save"] = Bool("Guardar asyncapi.yaml en la carpeta del sistema.") }, [], ReadOnly: false),
        new("create_service", "Crear un servicio",
            "Crea un servicio .NET nuevo (minimal API, health checks, ProblemDetails, tests...). " + PlanNote,
            new JsonObject
            {
                ["name"] = Text("Nombre en PascalCase, p. ej. \"Orders\"."),
                ["architecture"] = Enum("vertical-slice (por defecto, simple) o clean-ddd (agregados, comandos, capas).", "vertical-slice", "clean-ddd"),
                ["folder"] = Text("Carpeta, relativa al sistema o absoluta (por defecto: el nombre en kebab-case). Si queda fuera del sistema se añade a stratum.workspace.json."),
                ["disable"] = Features("Features que NO quieres (todas activas por defecto salvo auth)."),
                ["enable"] = Features("Features a activar además de las de por defecto (p. ej. auth)."),
                ["apply"] = Apply(),
            }, ["name"], ReadOnly: false),
        new("import_openapi", "Importar una especificación OpenAPI",
            "Crea un servicio (o añade entidades a uno existente) desde una especificación OpenAPI 3.x o Swagger 2.0, en JSON o YAML: recursos CRUD → entidades, propiedades que apuntan a otro recurso → referencias, POST /x/{id}/accion → operaciones (clean-ddd). Informa de lo que no puede traducir y de los endpoints que hay que escribir a mano. " + PlanNote,
            new JsonObject
            {
                ["spec"] = Text("Ruta del fichero (relativa al sistema o absoluta), URL, o el contenido de la especificación."),
                ["service"] = Service("Servicio existente al que añadir las entidades. Sin él se crea un servicio nuevo."),
                ["name"] = Text("Nombre del servicio nuevo (por defecto, el título de la especificación)."),
                ["architecture"] = Enum("Para un servicio nuevo.", "vertical-slice", "clean-ddd"),
                ["folder"] = Text("Carpeta del servicio nuevo, relativa al sistema o absoluta."),
                ["apply"] = Apply(),
            }, ["spec"], ReadOnly: false),
        new("add_entity", "Añadir una entidad",
            "Añade una entidad con su CRUD, configuración de EF Core y tests. " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["name"] = Text("Nombre en singular y PascalCase, p. ej. \"Product\"."),
                ["properties"] = Props(),
                ["plural"] = Text("Plural si no es el regular en inglés (Category → Categories ya lo hace solo)."),
                ["database"] = Enum("Solo para la primera entidad del servicio: decide su base de datos.", "postgres", "sqlserver"),
                ["apply"] = Apply(),
            }, ["service", "name", "properties"], ReadOnly: false),
        new("update_entity", "Cambiar una entidad",
            "Sustituye las propiedades de una entidad: las que no aparezcan se eliminan. Para renombrar sin perder datos usa \"renames\" (la migración tendrá RenameColumn). " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["name"] = Text("Entidad."),
                ["properties"] = Props(),
                ["renames"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Renombrados \"Viejo=Nuevo\" (el nuevo nombre debe estar en properties)." },
                ["apply"] = Apply(),
            }, ["service", "name", "properties"], ReadOnly: false),
        new("remove_entity", "Eliminar una entidad",
            "Elimina una entidad y su código generado. Los archivos del usuario se conservan salvo con deleteOwnFiles. " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["name"] = Text("Entidad."),
                ["deleteOwnFiles"] = Bool("Borrar también los archivos del usuario de esa entidad (partial classes, endpoints propios)."),
                ["apply"] = Apply(),
            }, ["service", "name"], ReadOnly: false, Destructive: true),
        new("add_relation", "Relacionar dos entidades",
            "Añade a una entidad una referencia a otra del mismo servicio (clave ajena): 1–N por defecto (muchos Order para un Customer), 1–1 con oneToOne (índice único) o N–M con many (una lista de ids: Customer.TagIds, un cliente con varias etiquetas y una etiqueta en varios clientes). onDelete decide qué pasa al borrar el destino: restrict (por defecto, no deja borrarlo mientras se use), cascade (borra los dependientes; en N–M, lo quita de las listas) o setNull (solo con optional). El listado gana el filtro ?<propiedad>= (en N–M, ?tagId=). Necesita una migración después. " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["entity"] = Text("Entidad que lleva la clave ajena, p. ej. Order."),
                ["target"] = Text("Entidad a la que apunta, p. ej. Customer."),
                ["property"] = Text("Nombre de la propiedad (por defecto <Destino>Id, p. ej. CustomerId)."),
                ["oneToOne"] = Bool("1–1: como mucho una entidad por cada destino."),
                ["many"] = Bool("N–M: una lista de ids del destino (por defecto la propiedad es <Destino>Ids)."),
                ["optional"] = Bool("La referencia admite null."),
                ["onDelete"] = Enum("Qué pasa al borrar el destino.", "restrict", "cascade", "setNull"),
                ["apply"] = Apply(),
            }, ["service", "entity", "target"], ReadOnly: false),
        new("update_relation", "Cambiar una relación",
            "Cambia una referencia existente: 1–1 o 1–N, opcional u obligatoria, y qué pasa al borrar el destino. Lo que no se indique se queda como está. " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["entity"] = Text("Entidad."),
                ["property"] = Text("Propiedad de la referencia, p. ej. CustomerId."),
                ["oneToOne"] = Bool("true: 1–1; false: 1–N."),
                ["optional"] = Bool("true: admite null; false: obligatoria."),
                ["onDelete"] = Enum("Qué pasa al borrar el destino.", "restrict", "cascade", "setNull"),
                ["apply"] = Apply(),
            }, ["service", "entity", "property"], ReadOnly: false),
        new("add_composition", "Componer un tipo de elemento en una entidad",
            "Mete un tipo de elemento dentro de una entidad: un solo elemento (columnas en la misma tabla, OwnsOne), opcional o no, o una lista (tabla propia, OwnsMany). El tipo se crea antes con set_type. " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["entity"] = Text("Entidad, p. ej. Customer."),
                ["property"] = Text("Propiedad, p. ej. Address."),
                ["type"] = Text("Tipo de elemento, p. ej. PostalAddress."),
                ["list"] = Bool("Una lista de elementos en lugar de uno."),
                ["optional"] = Bool("Un solo elemento que puede faltar (no vale para listas)."),
                ["apply"] = Apply(),
            }, ["service", "entity", "property", "type"], ReadOnly: false),
        new("remove_relation", "Quitar una relación o composición",
            "Quita de una entidad una referencia o un elemento compuesto (sus columnas o su tabla se borran en la migración). " + PlanNote,
            new JsonObject { ["service"] = Service(), ["entity"] = Text("Entidad."), ["property"] = Text("Propiedad."), ["apply"] = Apply() },
            ["service", "entity", "property"], ReadOnly: false, Destructive: true),
        new("set_rules", "Reglas de validación de una propiedad",
            "Valida una propiedad de una entidad (o un campo de un tipo de elemento). Textos: minLength, maxLength (también la longitud de la columna; por defecto 200), format (email, url, phone), pattern (expresión regular; requiere example, un valor válido que usan los tests y el .http). Números (int, long, decimal): min y max. La API responde 400 con el error por campo; en clean-ddd el dominio también lo comprueba. Lo que no se indique se queda como está; clear quita todas. " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["owner"] = Text("Entidad o tipo de elemento, p. ej. Customer."),
                ["property"] = Text("Propiedad, p. ej. Email."),
                ["minLength"] = new JsonObject { ["type"] = "integer", ["description"] = "Textos: longitud mínima." },
                ["maxLength"] = new JsonObject { ["type"] = "integer", ["description"] = "Textos: longitud máxima (y de la columna)." },
                ["format"] = Enum("Textos: formato.", "email", "url", "phone", "none"),
                ["pattern"] = Text("Textos: expresión regular que debe cumplir el valor completo, p. ej. ^\\d{5}$."),
                ["example"] = Text("Textos: un valor válido (obligatorio con pattern)."),
                ["min"] = new JsonObject { ["type"] = "number", ["description"] = "Números: valor mínimo." },
                ["max"] = new JsonObject { ["type"] = "number", ["description"] = "Números: valor máximo." },
                ["clear"] = Bool("Quita todas las reglas de la propiedad."),
                ["apply"] = Apply(),
            }, ["service", "owner", "property"], ReadOnly: false),
        new("set_enum", "Crear o cambiar un enumerado",
            "Crea un enumerado del servicio (p. ej. OrderStatus: Draft, Confirmed, Shipped) o sustituye sus valores. Se usa como tipo de propiedad (\"Status:OrderStatus\"); se guarda como texto. Quitar un valor que ya esté en la base de datos rompe esas filas: el plan lo avisa. " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["name"] = Text("Nombre en PascalCase, p. ej. OrderStatus."),
                ["values"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["minItems"] = 1, ["description"] = "Valores en PascalCase, en orden (el primero es el de por defecto)." },
                ["apply"] = Apply(),
            }, ["service", "name", "values"], ReadOnly: false),
        new("remove_enum", "Eliminar un enumerado",
            "Elimina un enumerado que no use ninguna propiedad. " + PlanNote,
            new JsonObject { ["service"] = Service(), ["name"] = Text("Enumerado."), ["apply"] = Apply() },
            ["service", "name"], ReadOnly: false, Destructive: true),
        new("set_type", "Crear o cambiar un tipo de elemento",
            "Crea un tipo de elemento para listas de objetos (p. ej. OrderItem: ProductId:guid, Quantity:int, UnitPrice:decimal) o sustituye sus propiedades (la lista completa). Se usa en una entidad como lista: \"Items:OrderItem[]\"; cada elemento se guarda en una tabla propia y la lista se reemplaza entera en el PUT. Sus propiedades: tipos básicos y enumerados (sin referencias ni listas). " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["name"] = Text("Nombre en PascalCase, p. ej. OrderItem."),
                ["properties"] = Text("Propiedades \"Nombre:tipo, ...\" (tipos básicos o enumerados; ? para opcional). Lista completa: las que falten se eliminan."),
                ["apply"] = Apply(),
            }, ["service", "name", "properties"], ReadOnly: false),
        new("remove_type", "Eliminar un tipo de elemento",
            "Elimina un tipo de elemento que no use ninguna lista. " + PlanNote,
            new JsonObject { ["service"] = Service(), ["name"] = Text("Tipo."), ["apply"] = Apply() },
            ["service", "name"], ReadOnly: false, Destructive: true),
        new("add_operation", "Añadir una operación al agregado",
            "Operación de negocio de un agregado (solo clean-ddd), p. ej. Order.Confirm o Order.Cancel(Reason): genera el evento de dominio, el comando, su handler y POST /<entidades>/{id}/<operación>, y un archivo del usuario <Entidad>.<Operación>.cs donde va la regla. " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["entity"] = Text("Entidad (agregado)."),
                ["name"] = Text("Verbo en PascalCase: Confirm, Cancel, Ship..."),
                ["parameters"] = Text("Datos que necesita, con el formato de properties (\"Reason:string, Amount:decimal?\"). Vacío si no necesita."),
                ["event"] = Text("Nombre del evento de dominio. Por defecto: entidad + pasado (OrderConfirmed)."),
                ["publish"] = Bool("Publicarla también como evento de integración propio para otros servicios (OrderShipped en orders.order-shipped), con los datos de la entidad y los parámetros. Requiere mensajería."),
                ["apply"] = Apply(),
            }, ["service", "entity", "name"], ReadOnly: false),
        new("publish_operation", "Publicar una operación para otros servicios",
            "Publica (o deja de publicar) una operación del agregado como evento de integración propio (OrderShipped), que otros servicios pueden escuchar con subscribe. " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["entity"] = Text("Entidad (agregado)."),
                ["name"] = Text("Operación."),
                ["publish"] = Bool("true para publicarla, false para dejar de publicarla."),
                ["apply"] = Apply(),
            }, ["service", "entity", "name", "publish"], ReadOnly: false),
        new("remove_operation", "Quitar una operación del agregado",
            "Quita una operación (su comando, endpoint y evento generados). El archivo del usuario con la regla se conserva. " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["entity"] = Text("Entidad (agregado)."),
                ["name"] = Text("Operación."),
                ["apply"] = Apply(),
            }, ["service", "entity", "name"], ReadOnly: false),
        new("set_features", "Activar o desactivar features",
            "Activa o desactiva features en un servicio existente (observability, openApi, resilience, auth, tests, docker, ci, mcp: servidor MCP del propio servicio en /mcp). " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["enable"] = Features("Features a activar."),
                ["disable"] = Features("Features a desactivar."),
                ["apply"] = Apply(),
            }, ["service"], ReadOnly: false),
        new("set_messaging", "Mensajería del servicio",
            "Activa la mensajería entre servicios (outbox, inbox, reintentos, dead-letter) con un broker, o la quita con \"off\". Después hay que crear la migración (tablas msg_outbox/msg_inbox) y regenerar el AppHost. " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["broker"] = Enum("kafka, azureServiceBus, inMemory (solo dentro del proceso) u off.", "kafka", "azureServiceBus", "inMemory", "off"),
                ["apply"] = Apply(),
            }, ["service", "broker"], ReadOnly: false),
        new("set_event_publishing", "Publicar eventos de entidades",
            "Hace que entidades publiquen <Entidad>Created/Updated/Deleted para otros servicios (o deja de hacerlo). El servicio necesita mensajería. " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["entities"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["minItems"] = 1, ["description"] = "Entidades." },
                ["publish"] = Bool("true para publicar, false para dejar de hacerlo."),
                ["apply"] = Apply(),
            }, ["service", "entities", "publish"], ReadOnly: false),
        new("subscribe", "Escuchar eventos de otro servicio",
            "El servicio escucha eventos de otro: copia el contrato y crea un handler que es del usuario (se ejecuta una vez por evento, con reintentos). " + PlanNote,
            new JsonObject
            {
                ["service"] = Service("Servicio que escucha."),
                ["publisher"] = Service("Servicio que publica."),
                ["event"] = Text("\"Order\" (Created, Updated y Deleted) o \"Order.Created\"."),
                ["apply"] = Apply(),
            }, ["service", "publisher", "event"], ReadOnly: false),
        new("add_replica", "Réplica de una entidad de otro servicio",
            "Crea en el servicio una copia local (tabla replica_<entidades>) de una entidad de otro servicio, al día con sus eventos Created, Updated y Deleted: un handler generado inserta, actualiza o borra la fila (un evento antiguo que llega tarde se ignora). Sirve para comprobar que existe o mostrar sus datos sin llamar al otro servicio. El publicador tiene que publicar los tres eventos; el servicio necesita base de datos (y se le activa la mensajería). Carga inicial: al arrancar con la réplica vacía, el servicio copia lo que ya existía leyendo la API del publicador (el AppHost generado le da la dirección; con autenticación, Replicas:<Publicador>:Authorization). Necesita una migración. Si ya hay una réplica de esa entidad, fields cambia los datos que copia. " + PlanNote,
            new JsonObject
            {
                ["service"] = Service("Servicio que guarda la copia."),
                ["publisher"] = Service("Servicio dueño de la entidad."),
                ["entity"] = Text("Entidad del publicador, p. ej. Customer."),
                ["fields"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Datos que se copian (además del Id), p. ej. [\"Name\", \"Email\"]. Por defecto, todos los datos simples del evento." },
                ["apply"] = Apply(),
            }, ["service", "publisher", "entity"], ReadOnly: false),
        new("remove_replica", "Quitar una réplica",
            "Quita la réplica (clase, tabla en el modelo y handler) y las suscripciones que solo ella usaba; tus handlers de esos eventos se quedan. Necesita una migración que borre la tabla. " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["publisher"] = Text("Nombre del servicio publicador, p. ej. Customers."),
                ["entity"] = Text("Entidad, p. ej. Customer."),
                ["apply"] = Apply(),
            }, ["service", "publisher", "entity"], ReadOnly: false, Destructive: true),
        new("unsubscribe", "Dejar de escuchar eventos",
            "Quita una suscripción (el handler del usuario se conserva). " + PlanNote,
            new JsonObject
            {
                ["service"] = Service("Servicio que escucha."),
                ["publisher"] = Text("Nombre del servicio que publica, p. ej. \"Orders\"."),
                ["event"] = Text("\"Order\" o \"Order.Created\"."),
                ["apply"] = Apply(),
            }, ["service", "publisher", "event"], ReadOnly: false),
        new("sync_subscriptions", "Actualizar contratos copiados",
            "Vuelve a copiar los contratos de los eventos que escucha un servicio si los publicadores cambiaron sus datos. " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["prune"] = Bool("Quitar las suscripciones a eventos que ya nadie publica."),
                ["apply"] = Apply(),
            }, ["service"], ReadOnly: false),
        new("review_migration", "Revisar la última migración",
            "Revisa la última migración de EF Core del servicio: convierte los renombrados en RenameColumn y avisa de lo que borra datos. " + PlanNote,
            new JsonObject { ["service"] = Service(), ["apply"] = Apply() }, ["service"], ReadOnly: false),
        new("generate_apphost", "Generar el AppHost de Aspire",
            "Genera o actualiza el AppHost de Aspire con todos los servicios, sus bases de datos y el broker (Kafka o emulador de Service Bus con topics y suscripciones). " + PlanNote,
            new JsonObject { ["name"] = Text("Nombre del sistema (AppHost <name>.AppHost). Por defecto el que ya tenga o el de la carpeta."), ["apply"] = Apply() },
            [], ReadOnly: false),
        new("set_deploy", "Destinos de despliegue del sistema",
            "Activa o desactiva dónde puede desplegarse el sistema con \"aspire deploy\": docker (Docker Compose en un servidor con Docker) o azure (Azure Container Apps, con Azure Database for PostgreSQL o Azure SQL y Service Bus). Se pueden tener los dos; entonces la variable STRATUM_DEPLOY elige al desplegar. Docker no admite Azure Service Bus. Sin apply muestra los destinos actuales. " + PlanNote,
            new JsonObject
            {
                ["add"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("docker", "azure") }, ["description"] = "Destinos que se activan." },
                ["remove"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("docker", "azure") }, ["description"] = "Destinos que se quitan." },
                ["apply"] = Apply(),
            }, [], ReadOnly: false),
        new("remove_service", "Eliminar un servicio",
            "Elimina un servicio y lo que cuelga de él: suscripciones de otros servicios, AppHost, stratum.workspace.json y su carpeta (salvo keepFiles). Con dropDatabase borra también su base de datos del contenedor del servidor (Docker o Podman; el servidor y las demás bases de datos se quedan). Irreversible fuera de git. " + PlanNote,
            new JsonObject
            {
                ["service"] = Service(),
                ["keepFiles"] = Bool("Conservar la carpeta del servicio en disco."),
                ["dropDatabase"] = Bool("Borrar también su base de datos (DROP DATABASE en el contenedor de PostgreSQL o SQL Server). Irreversible."),
                ["apply"] = Apply(),
            }, ["service"], ReadOnly: false, Destructive: true),
    ];

    public static bool Exists(string name) => All.Any(t => t.Name == name);

    public static JsonArray Definitions() => new([.. All.Select(t => (JsonNode)new JsonObject
    {
        ["name"] = t.Name,
        ["title"] = t.Title,
        ["description"] = t.Description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = t.Properties.DeepClone(),
            ["required"] = new JsonArray([.. t.Required.Select(r => (JsonNode)r)]),
            ["additionalProperties"] = false,
        },
        ["annotations"] = new JsonObject
        {
            ["title"] = t.Title,
            ["readOnlyHint"] = t.ReadOnly,
            ["destructiveHint"] = t.Destructive,
            ["idempotentHint"] = t.ReadOnly,
            ["openWorldHint"] = false,
        },
    })]);

    public ToolOutcome Call(string name, JsonObject args)
    {
        var apply = args["apply"]?.GetValue<bool>() ?? false;
        return name switch
        {
            "list_services" => ListServices(),
            "get_service" => new(File.ReadAllText(ServiceGenerator.FindDefinition(Folder(args, "service"))!)),
            "event_map" => EventMapOutcome(),
            "get_asyncapi" => AsyncApi(args["save"]?.GetValue<bool>() ?? false),
            "get_specification" => Specification(args),
            "generate_docs" => Cli(apply, ["spec", "--path", string.IsNullOrWhiteSpace(args["service"]?.GetValue<string>()) ? root : Folder(args, "service")]),
            "create_service" => CreateService(args, apply),
            "add_entity" => Cli(apply, ["add", "entity", Str(args, "name"), "--props", Str(args, "properties"), "--path", Folder(args, "service"),
                .. Optional(args, "plural", "--plural"), .. Optional(args, "database", "--db")]),
            "update_entity" => Cli(apply, ["entity", "update", Str(args, "name"), "--props", Str(args, "properties"), "--path", Folder(args, "service"),
                .. (args["renames"] as JsonArray ?? []).SelectMany(r => new[] { "--rename", r!.GetValue<string>() })]),
            "remove_entity" => Cli(apply, ["entity", "remove", Str(args, "name"), "--path", Folder(args, "service"),
                .. Flag(args, "deleteOwnFiles", "--delete-own-files")]),
            "import_openapi" => ImportOpenApi(args, apply),
            "add_relation" => Cli(apply, ["relation", "add", Optional(args, "property", "") is [_, var property] ? $"{Str(args, "entity")}.{property}" : Str(args, "entity"),
                Str(args, "target"), "--path", Folder(args, "service"), .. Flag(args, "oneToOne", "--one-to-one"), .. Flag(args, "many", "--many"), .. Flag(args, "optional", "--optional"),
                .. Optional(args, "onDelete", "--on-delete")]),
            "update_relation" => Cli(apply, ["relation", "update", $"{Str(args, "entity")}.{Str(args, "property")}", "--path", Folder(args, "service"),
                .. Choice(args, "oneToOne", "--one-to-one", "--one-to-many"), .. Choice(args, "optional", "--optional", "--required"),
                .. Optional(args, "onDelete", "--on-delete")]),
            "add_composition" => Cli(apply, ["relation", "compose", $"{Str(args, "entity")}.{Str(args, "property")}", Str(args, "type"),
                "--path", Folder(args, "service"), .. Flag(args, "list", "--list"), .. Flag(args, "optional", "--optional")]),
            "remove_relation" => Cli(apply, ["relation", "remove", $"{Str(args, "entity")}.{Str(args, "property")}", "--path", Folder(args, "service")]),
            "set_rules" => args["clear"]?.GetValue<bool>() == true
                ? Cli(apply, ["rule", "clear", $"{Str(args, "owner")}.{Str(args, "property")}", "--path", Folder(args, "service")])
                : Cli(apply, ["rule", "set", $"{Str(args, "owner")}.{Str(args, "property")}", "--path", Folder(args, "service"),
                    .. Number(args, "minLength", "--min-length"), .. Number(args, "maxLength", "--max-length"), .. Optional(args, "format", "--format"),
                    .. Optional(args, "pattern", "--pattern"), .. Optional(args, "example", "--example"), .. Number(args, "min", "--min"), .. Number(args, "max", "--max")]),
            "set_enum" => SetEnum(args, apply),
            "remove_enum" => Cli(apply, ["enum", "remove", Str(args, "name"), "--path", Folder(args, "service")]),
            "set_type" => SetType(args, apply),
            "remove_type" => Cli(apply, ["type", "remove", Str(args, "name"), "--path", Folder(args, "service")]),
            "add_operation" => Cli(apply, ["entity", "operation", "add", Str(args, "entity"), Str(args, "name"), "--path", Folder(args, "service"),
                .. Optional(args, "parameters", "--params"), .. Optional(args, "event", "--event"), .. Flag(args, "publish", "--publish")]),
            "publish_operation" => Cli(apply, ["entity", "operation", "publish", Str(args, "entity"), Str(args, "name"), "--path", Folder(args, "service"),
                .. (Bool(args, "publish") ? Array.Empty<string>() : ["--off"])]),
            "remove_operation" => Cli(apply, ["entity", "operation", "remove", Str(args, "entity"), Str(args, "name"), "--path", Folder(args, "service")]),
            "set_features" => SetFeatures(args, apply),
            "set_messaging" => Str(args, "broker") == "off"
                ? Cli(apply, ["messaging", "off", "--path", Folder(args, "service")])
                : Cli(apply, ["messaging", "on", "--broker", Str(args, "broker"), "--path", Folder(args, "service")]),
            "set_event_publishing" => Cli(apply, ["events", Bool(args, "publish") ? "on" : "off",
                .. (args["entities"] as JsonArray ?? []).Select(e => e!.GetValue<string>()), "--path", Folder(args, "service")]),
            "subscribe" => Cli(apply, ["subscribe", Folder(args, "publisher"), Str(args, "event"), "--path", Folder(args, "service")]),
            "unsubscribe" => Cli(apply, ["unsubscribe", Str(args, "publisher"), Str(args, "event"), "--path", Folder(args, "service")]),
            "add_replica" => AddReplica(args, apply),
            "remove_replica" => Cli(apply, ["replica", "remove", $"{Str(args, "publisher")}.{Str(args, "entity")}", "--path", Folder(args, "service")]),
            "sync_subscriptions" => Cli(apply, ["subscribe", "--sync", .. Flag(args, "prune", "--prune"), "--path", Folder(args, "service")]),
            "review_migration" => Cli(apply, ["migration", "review", "--path", Folder(args, "service")]),
            "generate_apphost" => AppHost(args, apply),
            "set_deploy" => SetDeploy(args, apply),
            "remove_service" => RemoveService(args, apply),
            _ => throw new StratumException($"Herramienta desconocida '{name}'."),
        };
    }

    /// <summary>add_replica: adds it, or changes its fields when the service has one of that entity already.</summary>
    private ToolOutcome AddReplica(JsonObject args, bool apply)
    {
        var directory = Folder(args, "service");
        var entity = Str(args, "entity");
        var fields = args["fields"] is JsonArray list ? string.Join(", ", list.Select(f => f!.GetValue<string>())) : null;
        var exists = (ServicePlanner.Load(directory).Replicas ?? []).Any(r => r.Entity.Equals(entity, StringComparison.OrdinalIgnoreCase));
        return Cli(apply, ["replica", exists ? "fields" : "add", Folder(args, "publisher"), entity, "--path", directory,
            .. fields is null ? Array.Empty<string>() : ["--fields", fields]]);
    }

    /// <summary>The specification (generated now from the model, so it's never stale) or one user story.</summary>
    private ToolOutcome Specification(JsonObject args)
    {
        if (string.IsNullOrWhiteSpace(args["service"]?.GetValue<string>()))
        {
            var services = AspireGenerator.Discover(root);
            return new(SpecGenerator.ForSystem(AspireGenerator.DefaultSystemName(root), [.. services.Select(s => (s.RelativeDirectory, s.Definition))]).Content);
        }

        var directory = Folder(args, "service");
        var files = SpecGenerator.ForService(directory, ServicePlanner.Load(directory));
        if (args["story"]?.GetValue<string>() is { Length: > 0 } story)
        {
            var file = files.FirstOrDefault(f => Path.GetFileName(f.Path).StartsWith(story.Trim() + "-", StringComparison.OrdinalIgnoreCase))
                ?? throw new StratumException($"No hay ninguna historia {story}. Las del servicio: {string.Join(", ", files.Where(f => f.Path.StartsWith(SpecGenerator.StoriesFolder, StringComparison.Ordinal)).Select(f => Path.GetFileNameWithoutExtension(f.Path)))}.");
            var onDisk = Path.Combine(directory, file.Path);
            return new(File.Exists(onDisk) ? File.ReadAllText(onDisk) : file.Content + "\n\n(Aún no está en disco: generate_docs la crea.)");
        }

        var text = new StringBuilder(files[0].Content);
        text.AppendLine();
        text.AppendLine("## Historias de usuario");
        text.AppendLine();
        foreach (var file in files.Where(f => f.Path.StartsWith(SpecGenerator.StoriesFolder, StringComparison.Ordinal)))
        {
            var exists = File.Exists(Path.Combine(directory, file.Path));
            text.AppendLine($"- {file.Path}{(exists ? string.Empty : " (aún no está en disco)")}");
        }

        var context = Path.Combine(directory, SpecGenerator.ContextPath);
        if (File.Exists(context))
        {
            text.AppendLine();
            text.AppendLine(File.ReadAllText(context));
        }

        return new(text.ToString());
    }

    private ToolOutcome ListServices()
    {
        var services = AspireGenerator.Discover(root);
        var list = new JsonArray();
        var text = new StringBuilder();
        if (services.Count == 0)
        {
            text.AppendLine($"No hay servicios Stratum en {root}. Crea uno con create_service.");
        }

        foreach (var service in services)
        {
            var d = service.Definition;
            var features = FeatureSet.Names.Where(d.Features.IsEnabled).ToList();
            var entities = d.Entities.Select(e => new JsonObject
            {
                ["name"] = e.Name,
                ["plural"] = e.Plural,
                ["properties"] = PropertyTypes.Spec(e.Properties),
                ["publishesEvents"] = e.Publishes,
                ["operations"] = new JsonArray([.. (e.Operations ?? []).Select(o => (JsonNode)$"{o.Name}({PropertyTypes.Spec(o.Parameters)}) → {o.Event}")]),
                ["editable"] = e.IsEditable,
            }).ToList();
            var subscriptions = (d.Subscriptions ?? []).Select(s => s.Topic).ToList();
            list.Add(new JsonObject
            {
                ["name"] = d.Service.Name,
                ["folder"] = Path.GetFullPath(Path.Combine(root, service.RelativeDirectory)),
                ["architecture"] = d.Architecture,
                ["database"] = d.Persistence?.Provider,
                ["broker"] = d.Messaging?.Broker,
                ["features"] = new JsonArray([.. features.Select(f => (JsonNode)f)]),
                ["entities"] = new JsonArray([.. entities.Cast<JsonNode>()]),
                ["subscribesTo"] = new JsonArray([.. subscriptions.Select(s => (JsonNode)s)]),
                ["enums"] = new JsonArray([.. (d.Enums ?? []).Select(e => (JsonNode)$"{e.Name}: {string.Join(", ", e.Values)}")]),
                ["types"] = new JsonArray([.. (d.Types ?? []).Select(t => (JsonNode)$"{t.Name}: {PropertyTypes.Spec(t.Properties)}")]),
            });

            text.AppendLine($"{d.Service.Name} ({d.Architecture}{(d.Persistence is { } p ? ", " + p.Provider : "")}{(d.Messaging is { } m ? ", " + m.Broker : "")}) en {service.RelativeDirectory}");
            text.AppendLine($"  features: {string.Join(", ", features)}");
            foreach (var e in d.Enums ?? [])
            {
                text.AppendLine($"  enum {e.Name}: {string.Join(", ", e.Values)}");
            }
            foreach (var t in d.Types ?? [])
            {
                text.AppendLine($"  tipo {t.Name}: {PropertyTypes.Spec(t.Properties)}");
            }
            foreach (var e in d.Entities)
            {
                text.AppendLine($"  {e.Name}: {PropertyTypes.Spec(e.Properties)}{(e.Publishes ? "  [publica eventos]" : "")}{(e.IsEditable ? "" : "  [anterior a 0.10: entity convert]")}");
                foreach (var o in e.Operations ?? [])
                {
                    text.AppendLine($"    operación {o.Name}({PropertyTypes.Spec(o.Parameters)}) → {o.Event}");
                }
            }

            foreach (var s in subscriptions)
            {
                text.AppendLine($"  escucha {s}");
            }
        }

        return new(text.ToString().TrimEnd(), new JsonObject { ["root"] = root, ["services"] = list });
    }

    private ToolOutcome EventMapOutcome()
    {
        var routes = EventMap.Build(AspireGenerator.Discover(root).Select(s => s.Definition));
        var output = new StringWriter();
        global::Stratum.Cli.Cli.Run(["events", "map", "--path", root], output);
        var structured = new JsonArray([.. routes.Select(r => (JsonNode)new JsonObject
        {
            ["topic"] = r.Topic,
            ["event"] = r.EventClass,
            ["publisher"] = r.Publisher,
            ["consumers"] = new JsonArray([.. r.Consumers.Select(c => (JsonNode)c)]),
            ["problem"] = r.Problem,
        })]);
        return new(output.ToString().TrimEnd(), new JsonObject { ["events"] = structured });
    }

    private ToolOutcome AsyncApi(bool save)
    {
        if (save)
        {
            var output = new StringWriter();
            global::Stratum.Cli.Cli.Run(["events", "asyncapi", "--path", root], output);
            return new(output.ToString().TrimEnd() + "\n\n" + File.ReadAllText(Path.Combine(root, "asyncapi.yaml")));
        }

        return new(AsyncApiDocument.ToYaml(AsyncApiDocument.Build(AspireGenerator.Discover(root).Select(s => s.Definition), $"{new DirectoryInfo(root).Name} events")));
    }

    private ToolOutcome CreateService(JsonObject args, bool apply)
    {
        var name = Str(args, "name");
        var folder = args["folder"]?.GetValue<string>() is { Length: > 0 } f ? Path.GetFullPath(Path.Combine(root, f)) : Path.Combine(root, ServiceName.ToKebab(name));
        List<string> cli = ["new", name, "-o", folder, .. Optional(args, "architecture", "--architecture")];
        cli.AddRange((args["disable"] as JsonArray ?? []).Select(x => "--no-" + x!.GetValue<string>().ToLowerInvariant()));
        cli.AddRange((args["enable"] as JsonArray ?? []).Select(x => "--" + x!.GetValue<string>().ToLowerInvariant()));
        if (packageSource is not null)
        {
            cli.AddRange(["--package-source", packageSource]);
        }

        var outcome = Cli(apply, [.. cli]);
        if (!apply)
        {
            outcome = outcome with { Text = $"Vista previa: servicio {name} en {folder}, con estos archivos:\n" + outcome.Text };
        }

        var outside = !folder.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (outside)
        {
            if (apply)
            {
                WorkspaceFile.Add(root, folder);
                return outcome with { Text = outcome.Text + $"\n\nAñadido a stratum.workspace.json: {folder}" };
            }

            return outcome with { Text = outcome.Text + $"\n\nLa carpeta está fuera del sistema: al aplicar se añadirá a stratum.workspace.json." };
        }

        return outcome;
    }

    private ToolOutcome SetEnum(JsonObject args, bool apply)
    {
        var folder = Folder(args, "service");
        var name = Str(args, "name");
        var values = string.Join(", ", (args["values"] as JsonArray ?? []).Select(v => v!.GetValue<string>()));
        var exists = ServicePlanner.Load(folder).EnumNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        return Cli(apply, ["enum", exists ? "update" : "add", name, "--values", values, "--path", folder]);
    }

    private ToolOutcome SetType(JsonObject args, bool apply)
    {
        var folder = Folder(args, "service");
        var name = Str(args, "name");
        var exists = ServicePlanner.Load(folder).TypeNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        return Cli(apply, ["type", exists ? "update" : "add", name, "--props", Str(args, "properties"), "--path", folder]);
    }

    private ToolOutcome ImportOpenApi(JsonObject args, bool apply)
    {
        var spec = Str(args, "spec");
        string? temp = null;
        var trimmed = spec.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith("openapi", StringComparison.Ordinal) || trimmed.StartsWith("swagger", StringComparison.Ordinal) || spec.Contains('\n', StringComparison.Ordinal))
        {
            temp = Path.Combine(Path.GetTempPath(), $"stratum-mcp-{Guid.NewGuid():N}{(trimmed.StartsWith('{') ? ".json" : ".yaml")}");
            File.WriteAllText(temp, spec);
            spec = temp;
        }
        else if (!spec.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            spec = Path.GetFullPath(Path.Combine(root, spec));
        }

        try
        {
            List<string> cli = ["import", "openapi", spec];
            if (args["service"]?.GetValue<string>() is { Length: > 0 })
            {
                cli.AddRange(["--path", Folder(args, "service")]);
            }
            else
            {
                var name = args["name"]?.GetValue<string>();
                var folder = args["folder"]?.GetValue<string>() is { Length: > 0 } f
                    ? Path.GetFullPath(Path.Combine(root, f))
                    : name is { Length: > 0 } ? Path.Combine(root, ServiceName.ToKebab(ServiceName.Normalize(name))) : null;
                cli.AddRange([.. Optional(args, "name", "--name"), .. Optional(args, "architecture", "--architecture")]);
                if (folder is not null)
                {
                    cli.AddRange(["-o", folder]);
                }

                if (packageSource is not null)
                {
                    cli.AddRange(["--package-source", packageSource]);
                }
            }

            return Cli(apply, [.. cli]);
        }
        finally
        {
            if (temp is not null)
            {
                File.Delete(temp);
            }
        }
    }

    private ToolOutcome SetFeatures(JsonObject args, bool apply)
    {
        var folder = Folder(args, "service");
        var enable = (args["enable"] as JsonArray ?? []).Select(x => x!.GetValue<string>()).ToArray();
        var disable = (args["disable"] as JsonArray ?? []).Select(x => x!.GetValue<string>()).ToArray();
        if (enable.Length + disable.Length == 0)
        {
            throw new StratumException("Indica al menos una feature en enable o disable.");
        }

        var text = new StringBuilder();
        if (enable.Length > 0)
        {
            text.AppendLine(Cli(apply, ["feature", "on", .. enable, "--path", folder]).Text);
        }

        if (disable.Length > 0)
        {
            // Planned against the current stratum.json: when only planning, the "on" part above has not been written.
            text.AppendLine(Cli(apply, ["feature", "off", .. disable, "--path", folder]).Text);
        }

        return new(text.ToString().TrimEnd());
    }

    private ToolOutcome AppHost(JsonObject args, bool apply)
    {
        var services = AspireGenerator.Discover(root);
        if (!apply)
        {
            var text = new StringBuilder($"Plan: generar o actualizar el AppHost en {root} con {services.Count} servicio(s): {string.Join(", ", services.Select(s => s.Name))}.\n");
            var brokers = services.Select(s => s.Definition.Messaging?.Broker).Where(b => b is MessagingSettings.Kafka or MessagingSettings.AzureServiceBus).Distinct().ToList();
            if (brokers.Count > 0)
            {
                text.AppendLine($"Brokers: {string.Join(", ", brokers)} (contenedores de Aspire).");
            }

            text.AppendLine("Solo se reescriben los .g.cs del AppHost y se añaden paquetes o referencias que falten; Program.cs es del usuario.");
            text.Append("Repite con \"apply\": true para generarlo.");
            return new(text.ToString());
        }

        return Run(["aspire", "--path", root, .. Optional(args, "name", "--name")]);
    }

    private ToolOutcome SetDeploy(JsonObject args, bool apply)
    {
        static string[] Targets(JsonObject args, string key) => args[key] is JsonArray list ? [.. list.Select(t => t!.GetValue<string>())] : [];
        var add = Targets(args, "add");
        var remove = Targets(args, "remove");
        if (!apply || (add.Length == 0 && remove.Length == 0))
        {
            var output = new StringWriter();
            global::Stratum.Cli.Cli.Run(["deploy", "list", "--path", root], output);
            return new(output.ToString().TrimEnd() + (add.Length + remove.Length == 0 ? string.Empty
                : $"\nSe {(add.Length > 0 ? "activaría " + string.Join(", ", add) : string.Empty)}{(add.Length > 0 && remove.Length > 0 ? " y se " : string.Empty)}{(remove.Length > 0 ? "quitaría " + string.Join(", ", remove) : string.Empty)}. Repite con \"apply\": true."));
        }

        var text = new StringBuilder();
        if (add.Length > 0)
        {
            text.Append(Run(["deploy", "add", .. add, "--path", root]).Text).AppendLine();
        }

        if (remove.Length > 0)
        {
            text.Append(Run(["deploy", "remove", .. remove, "--path", root]).Text);
        }

        return new(text.ToString().TrimEnd());
    }

    private ToolOutcome RemoveService(JsonObject args, bool apply)
    {
        List<string> cli = ["service", "remove", Folder(args, "service"), "--path", root, .. Flag(args, "keepFiles", "--keep-files"), .. Flag(args, "dropDatabase", "--drop-database")];
        if (apply)
        {
            cli.Add("--yes");
        }

        var outcome = Run([.. cli]);
        return apply ? outcome : outcome with
        {
            Text = outcome.Text.Replace("Para eliminarlo de verdad, repite el comando con --yes.", "Repite con \"apply\": true para eliminarlo.", StringComparison.Ordinal),
        };
    }

    /// <summary>Runs a CLI command, adding --dry-run unless applying.</summary>
    private static ToolOutcome Cli(bool apply, string[] args)
    {
        var outcome = Run(apply ? args : [.. args, "--dry-run"]);
        return apply ? outcome : outcome with { Text = outcome.Text + "\n\n(Plan: no se ha escrito nada. Repite con \"apply\": true para aplicarlo.)" };
    }

    private static ToolOutcome Run(string[] args)
    {
        var output = new StringWriter();
        var code = global::Stratum.Cli.Cli.Run(args, output);
        if (code != 0)
        {
            throw new StratumException(output.ToString().Trim());
        }

        return new(output.ToString().TrimEnd());
    }

    /// <summary>A service by name ("Orders") or folder (relative to the system or absolute).</summary>
    private string Folder(JsonObject args, string key)
    {
        var value = Str(args, key);
        var services = AspireGenerator.Discover(root);
        if (services.FirstOrDefault(s => string.Equals(s.Name, value, StringComparison.OrdinalIgnoreCase)) is { } byName)
        {
            return Path.GetFullPath(Path.Combine(root, byName.RelativeDirectory));
        }

        var path = Path.GetFullPath(Path.Combine(root, value));
        if (ServiceGenerator.FindDefinition(path) is not null)
        {
            return path;
        }

        throw new StratumException($"No hay ningún servicio '{value}'. Servicios: {(services.Count == 0 ? "ninguno" : string.Join(", ", services.Select(s => s.Name)))}.");
    }

    private static string Str(JsonObject args, string key) =>
        args[key]?.GetValue<string>() is { Length: > 0 } value ? value : throw new StratumException($"Falta el argumento \"{key}\".");

    private static bool Bool(JsonObject args, string key) =>
        args[key]?.GetValue<bool>() ?? throw new StratumException($"Falta el argumento \"{key}\".");

    private static string[] Optional(JsonObject args, string key, string option) =>
        args[key]?.GetValue<string>() is { Length: > 0 } value ? [option, value] : [];

    /// <summary>A JSON number (or a numeric string) as a CLI option, invariant culture.</summary>
    private static string[] Number(JsonObject args, string key, string option) => args[key] switch
    {
        JsonValue v when v.TryGetValue<decimal>(out var d) => [option, d.ToString(System.Globalization.CultureInfo.InvariantCulture)],
        JsonValue v when v.TryGetValue<string>(out var text) && text.Length > 0 => [option, text],
        _ => [],
    };

    private static string[] Choice(JsonObject args, string key, string whenTrue, string whenFalse) =>
        args[key]?.GetValue<bool>() switch { true => [whenTrue], false => [whenFalse], null => [] };

    private static string[] Flag(JsonObject args, string key, string option) =>
        args[key]?.GetValue<bool>() == true ? [option] : [];

    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject Apply() => new()
    {
        ["type"] = "boolean",
        ["default"] = false,
        ["description"] = "false: devuelve el plan sin escribir nada. true: lo aplica (solo tras la aprobación del usuario).",
    };

    private static JsonObject Service(string description = "Servicio: su nombre (\"Orders\") o su carpeta.") => Text(description);

    private static JsonObject Props() => Text("Propiedades \"Nombre:tipo, ...\" (string, int, long, decimal, bool, guid, date, datetime; ? para opcional). Para una referencia a otra entidad del mismo servicio (clave ajena, se comprueba que existe) usa su nombre como tipo: \"CustomerId:Customer\" o \"ParentId:Category?\". Para un enumerado del servicio (set_enum), su nombre: \"Status:OrderStatus\". Listas con []: \"Tags:string[]\", \"Statuses:OrderStatus[]\" o de objetos con un tipo de elemento (set_type): \"Items:OrderItem[]\". Id, CreatedAt y UpdatedAt ya existen.");

    private static JsonObject Enum(string description, params string[] values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]),
        ["description"] = description,
    };

    private static JsonObject Features(string description) => new()
    {
        ["type"] = "array",
        ["items"] = Enum("Feature.", [.. FeatureSet.Names]),
        ["description"] = description,
    };
}
