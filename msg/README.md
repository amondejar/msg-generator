# Stratum: generador de microservicios .NET

Stratum genera microservicios .NET 10 desde la línea de comandos o desde un editor visual (`stratum studio`) que nacen con lo que cualquier equipo serio exige siempre igual: estructura, observabilidad, errores, configuración validada, tests, Docker y CI. Lo común no se copia en cada servicio: llega en los paquetes `Stratum.ServiceDefaults.*`, así que mejorarlo o parchearlo es subir una versión. El objetivo final es un editor visual.

> **Nombre:** la herramienta se llamaba Stratum y ahora es **Stratum** (comando `stratum`). Los paquetes `Stratum.*`, el archivo
> `stratum.json` y la carpeta `.stratum` mantienen su nombre por ahora; se renombrarán antes de publicar en NuGet.

### Stratum Studio

`stratum studio --path <carpeta del sistema>` abre el editor en el navegador. El menú lateral tiene dos niveles:

- **Sistema:** Resumen (cifras y accesos rápidos), Servicios (crear, en cualquier carpeta, o añadir uno existente),
  Mapa de eventos, Ejecutar (Aspire) y Plantillas del equipo.
- **Servicio** (el que eliges en el selector): General (arquitectura y features), Entidades, Datos y migraciones,
  Mensajería (broker, eventos que publica y que escucha), Código generado y Revisar y aplicar.

Nada se escribe en disco hasta que revisas los cambios (con su diff) y los aplicas.

```bash
stratum new Orders                 # servicio completo (vertical slice)
stratum new Orders --architecture clean-ddd   # Clean Architecture + DDD con eventos de dominio
stratum new Billing --auth         # + JWT, autenticado por defecto
stratum new Catalog --no-docker    # quita lo que no necesites
stratum new --from stratum.json        # regenera desde una definición
stratum new Orders --dry-run       # lista los archivos sin escribir nada
stratum upgrade                    # actualiza los paquetes Stratum.* de un servicio (o de todo el sistema) a la versión instalada

# Dentro de un servicio:
stratum add entity Product --props "Name:string, Price:decimal, Description:string?"
stratum add entity Invoice --props "Number:string, Total:decimal" --db sqlserver   # la primera entidad elige la BD
```

## Servicios de antes de la 0.22 (`Msg.*`, `msg.json`)

Hasta la 0.21 los paquetes se llamaban `Msg.*`, la definición `msg.json` y la carpeta del equipo `.msg`. Esos servicios se
siguen leyendo (Studio, mapa de eventos, AppHost), pero para cambiarlos hay que pasarlos antes a los nombres nuevos:

```bash
stratum upgrade --dry-run          # en la carpeta del sistema: qué cambia en cada servicio, sin escribir nada
stratum upgrade                    # o --path <servicio> para uno solo
```

- `msg.json` → `stratum.json`, `msg.workspace.json` → `stratum.workspace.json`, `.msg` → `.stratum`.
- En tu código: `using Msg.ServiceDefaults/Ddd/Messaging` → `Stratum.…`, `AddMsgWiring()` → `AddStratumWiring()`,
  `AddMsgServices()` en el AppHost y los demás métodos `AddMsg*`/`UseMsg*`/`MapMsg*` de los paquetes.
- Paquetes `Msg.*` → `Stratum.*`; los `.g.cs` (`MsgWiring.g.cs` → `StratumWiring.g.cs`) y el AppHost se generan de nuevo.
- No cambian, a propósito: las tablas `msg_outbox`/`msg_inbox` (tu base de datos sigue igual) ni las cabeceras de los
  mensajes (`msg-source`…), así que un servicio migrado y otro sin migrar se siguen entendiendo.
- Las plantillas del equipo aceptan `@msg:if` y `@stratum:if`.

## Arquitecturas

| | `vertical-slice` (por defecto) | `clean-ddd` |
| --- | --- | --- |
| Proyectos | `Api` | `Domain`, `Application`, `Infrastructure`, `Api` |
| Entidades | Clases con propiedades | Agregados (`AggregateRoot<Guid>`) con `Create`/`Update`/`Delete`, reglas en `Apply` y `DomainException` → 422 |
| Lógica | En el endpoint, con el `DbContext` | Commands y queries con sus handlers, que el endpoint recibe por DI (sin mediator) |
| Eventos | No hay | `XCreated`/`XUpdated`/`XDeleted`, publicados tras el commit por un interceptor de EF Core |
| Tests | Integración | Integración + tests de dominio sin base de datos |

Los bloques de construcción de DDD están en el paquete `Stratum.Ddd`, que no tiene dependencias, y los puede referenciar el proyecto Domain.
No usamos MediatR: desde la v13 tiene licencia dual (RPL-1.5 o comercial). Los handlers se registran con `AddStratumApplication()`.

## `stratum studio`

```bash
stratum studio                       # en la carpeta que contiene tus servicios (o --path <carpeta>)
```

Abre el editor visual en `http://localhost:5150`. Usa el mismo motor que la CLI, así que lo que ves es lo que se genera:

- **Sistema**: tus servicios, crear uno nuevo y generar o actualizar el AppHost de Aspire.
- **Servicio**: arquitectura, features y base de datos, con vista previa en vivo de `Program.cs`, el árbol de archivos y `stratum.json`.
- **Entidades**: propiedades, operaciones del agregado (Clean + DDD), enumerados y tipos de elemento, en tablas de lectura.
  **+ Añadir…** o **✎** abre una ventana flotante (se mueve arrastrando su título) donde lo configuras con su vista previa;
  **Guardar y cerrar** lo pasa a los cambios pendientes y **Cerrar** (o Esc) lo descarta. Desde la ventana de una propiedad
  puedes crear el enumerado o el tipo de elemento que te falte, y queda elegido. A la derecha, el código generado de cada capa.
- **Aplicar cambios**: qué archivos se crean, cuáles se regeneran y cuáles se completan, con el diff de cada uno. Nunca se sobrescribe un archivo tuyo.
- **Ejecutar sistema** (en Sistema): genera o actualiza el AppHost y lo arranca con Aspire. Muestra la salida en vivo, el enlace al
  dashboard y los de Scalar y health de cada servicio. Avisa si falta Docker; al cerrar el Studio se detiene todo.
- **Migraciones** (en Servicio): lista las migraciones y crea la siguiente con un nombre sugerido (`Initial`, `AddProduct`…),
  con los proyectos correctos según la arquitectura. Si falta `dotnet-ef`, ofrece instalarlo.

En un servicio que ya existe solo se pueden añadir entidades: las existentes, la arquitectura y las features ya son código tuyo.
Opciones: `--port`, `--no-browser` y `--package-source` (igual que en `stratum new`).

## Plantillas del equipo (`.stratum`)

Stratum trae sus convenciones, pero las que mandan son las de tu equipo. Pon una carpeta `.stratum` en la raíz de tu repo
(o de tu carpeta de servicios) y haz commit. `stratum new`, `stratum add entity` y `stratum studio` la encuentran subiendo de carpeta:

```bash
stratum templates list                                            # plantillas de Stratum y del equipo
stratum templates eject service/src/__Service__.Api/Program.cs     # copia una a .stratum/templates para editarla
stratum templates eject entity-clean                               # o un set entero
```

| En `.stratum/templates/<set>/…` | Efecto |
| --- | --- |
| Misma ruta que una plantilla de Stratum | La sustituye |
| Ruta nueva (p. ej. `service/CODEOWNERS`) | Se añade a lo que genera Stratum |
| Archivo vacío | Esa plantilla no se genera (p. ej. `service/Dockerfile`) |

Sets: `service`, `entity` (vertical slice), `entity-clean` y `entity-tests`. Las plantillas usan los tokens y las
condiciones de Stratum (`{{Service}}`, `{{Entity}}`, `@stratum:if auth`…). Además, `.stratum/config.json` define tokens propios:

```json
{ "tokens": { "Company": "Acme" } }
```

Con eso, `{{Company}}` sirve en cualquier plantilla. Los tokens de Stratum no se pueden redefinir. Un error en una plantilla
del equipo indica el archivo y la línea. El Studio muestra qué plantillas del equipo están activas y las usa en la vista previa.

## `stratum aspire`

En la carpeta que contiene tus servicios:

```bash
stratum aspire --name Shop     # genera Shop.AppHost, Shop.slnx y .aspire/settings.json
aspire run                 # o: dotnet run --project Shop.AppHost  (requiere Docker o Podman)
```

- Un recurso por servicio, con health check en `/health/ready`.
- PostgreSQL y/o SQL Server según la base de datos de cada servicio (volúmenes persistentes), con su cadena de conexión
  inyectada como `ConnectionStrings__Database` y `Database__MigrateOnStartup=true`: aplica las migraciones al arrancar
  o, si aún no hay ninguna, crea el esquema.
- La telemetría aparece sola en el dashboard: Aspire pasa `OTEL_EXPORTER_OTLP_ENDPOINT` y `AddStratumOpenTelemetry()` la usa.
- Enlace directo a Scalar en el dashboard para cada servicio con OpenAPI.
- Service discovery: en `AppHost.cs`, `services.Billing.WithReference(services.Orders)` y Billing llama a `http://orders`.
- `StratumServices.g.cs` y la `.slnx` se regeneran en cada `stratum aspire`; `AppHost.cs` es tuyo. Volver a ejecutarlo incorpora los servicios nuevos.

AppHost en Aspire 13.6.5 (la línea validada con .NET 10). Para pasar a Aspire 17: `aspire update`.

## `stratum add entity`

Por cada entidad genera la entidad (Id UUID v7, `CreatedAt`, `UpdatedAt`), su `IEntityTypeConfiguration`, los DTOs con validación
(400 ProblemDetails gracias a la validación integrada de .NET 10), los endpoints CRUD paginados y sus tests. La primera entidad añade
además el `DbContext`, el health check de base de datos (readiness), `compose.yaml` y una base SQLite en memoria para los tests.

Tipos de `--props`: `string`, `int`, `long`, `decimal`, `bool`, `guid`, `date`, `datetime`. Con `?` la propiedad es opcional.

Reglas para no pisar tu código:

| Archivos | Qué hace Stratum |
| --- | --- |
| `*.g.cs`: propiedades, configuración EF, contratos, CRUD, comandos/queries, tests CRUD, registros, `DbSet`s | Los regenera siempre a partir de `stratum.json`. |
| Clases `partial` sin `.g` (`Product.cs`, `ProductEndpoints.cs`), handlers de eventos, repositorio, `DbContext`, `compose.yaml` | Los crea una vez. A partir de ahí son tuyos. |
| `.csproj`, `Directory.Packages.props`, `appsettings.Development.json` | Solo añade lo que falta. |
| Todo lo demás | No lo toca. |

Tu código va en las clases `partial`: métodos y propiedades calculadas en la entidad, reglas en `EnsureInvariants()` (clean-ddd),
mapeo EF extra en `Customize(builder)` y endpoints propios en `MapCustomEndpoints(group)`.

La entidad queda en `stratum.json`, así que `stratum new --from stratum.json` reconstruye el servicio con todas sus entidades.

## Cambiar entidades (`stratum entity`)

```bash
stratum entity list
stratum entity update Product --props "Title:string, Price:decimal, Stock:int" --rename Name=Title --dry-run
stratum entity remove Tag                      # tus archivos de Tag se quedan (y te los lista)
stratum entity remove Tag --delete-own-files   # también los borra
stratum entity convert Product                 # entidades creadas antes de Stratum 0.10
```

- `--props` es la lista completa: lo que no esté se elimina. `--rename Viejo=Nuevo` marca un renombrado; queda en `pendingRenames`
  de `stratum.json` para que la migración conserve los datos (`RenameColumn` en lugar de borrar y crear).
- Si tu código usa una propiedad eliminada o renombrada, Stratum te dice en qué archivos.
- En el Studio es lo mismo: cambia el nombre en la fila para renombrar, borra filas, añade otras o elimina la entidad; la pantalla
  de revisión enseña el diff y cuántos archivos tuyos se tocan (solo al convertir o con "borrar también mis archivos").
- Las entidades creadas antes de la 0.10 tenían todo en archivos tuyos: Stratum no las edita hasta que las conviertes. La conversión
  reemplaza la entidad y los endpoints por clases `partial` vacías y borra contratos, configuración, comandos, queries y tests
  (pasan a `.g.cs`); eventos, handlers y repositorio se quedan como están. Revisa el `--dry-run` y pasa tus cambios a las partial.

### Referencias entre entidades

```bash
stratum add entity Order --props "Number:string, CustomerId:Customer, ParentId:Order?"
```

El tipo de una propiedad puede ser otra entidad del servicio (`Customer:Customer` se convierte en `CustomerId`). Stratum genera:

- La clave ajena (`HasOne<Customer>().WithMany().HasForeignKey(...)`, sin borrado en cascada).
- Al crear o actualizar, comprueba que el `Customer` existe: 400 con el campo en vertical slice, 422 en clean-ddd.
- Al borrar un `Customer` que aún usan pedidos: 409 en vertical slice, 422 en clean-ddd (en lugar de un error de la base de datos).
- Tests que crean antes por la API lo que hace falta (el `Customer` de un `Order`) y uno que comprueba el rechazo de un Id que no existe.
- No se puede eliminar una entidad mientras otra apunte a ella, ni crear referencias obligatorias en círculo (haz una opcional).
- En el Studio, el tipo de la propiedad ofrece `→ Customer`.
- Entre servicios no hay claves ajenas: guarda el Id como `guid` y mantén los datos con [eventos](#escuchar-eventos-de-otro-servicio).

### Enumerados

```bash
stratum enum add OrderStatus --values "Draft, Confirmed, Shipped"
stratum entity update Order --props "Number:string, Status:OrderStatus, Previous:OrderStatus?"
stratum enum update OrderStatus --values "Draft, Confirmed, Shipped, Cancelled"
stratum enum list
```

- Stratum genera el `enum` de C# y lo puedes usar como tipo de propiedad o de parámetro de una operación.
- Se guarda como **texto** en la base de datos (`HasConversion<string>()`) y en el JSON de la API, así que añadir o
  reordenar valores no rompe datos. Un valor desconocido en una petición devuelve 400.
- En los eventos de integración viaja como texto: quien escucha no depende del enum del que publica, y un valor nuevo no
  le rompe nada. AsyncAPI documenta los valores posibles.
- Quitar un valor avisa en el plan, porque las filas que ya lo tengan guardado fallarán al leerse. No se puede eliminar
  un enumerado mientras alguna propiedad lo use.
- En el Studio: **Entidades → Enumerados del servicio**, y en el selector de tipo aparecen como `# OrderStatus`.
- `stratum import openapi` convierte los `enum` de texto de la especificación en enumerados, y la herramienta MCP
  `set_enum` hace lo mismo desde un asistente.

### Listas

```bash
# Listas de valores: tipo básico o enumerado con []
stratum entity update Product --props "Name:string, Tags:string[], Sizes:Size[]"

# Listas de objetos: primero el tipo de elemento, luego la lista
stratum type add OrderItem --props "ProductId:guid, Quantity:int, UnitPrice:decimal, Status:LineStatus"
stratum entity update Order --props "Number:string, Items:OrderItem[]"
stratum type update OrderItem --props "ProductId:guid, Quantity:int, UnitPrice:decimal, Status:LineStatus, Notes:string?"
stratum type list
```

- **Listas de valores** (`string[]`, `int[]`, `OrderStatus[]`…): una columna JSON en la tabla de la entidad (en
  PostgreSQL, un array nativo). Los enumerados se guardan como texto.
- **Listas de objetos** (`OrderItem[]`): cada elemento es una fila de una tabla propia (`order_items`, enlazada con
  `OrderId`) y forma parte de la entidad: no tiene endpoints propios, se lee con ella y se borra con ella. El tipo de
  elemento admite tipos básicos y enumerados (sin referencias ni listas dentro).
- En la API la lista va dentro de la entidad: `"items": [{ "productId": "…", "quantity": 2, "unitPrice": 9.95 }]`. El
  `PUT` **reemplaza la lista entera**; si el JSON no trae la lista, queda vacía. Los campos de texto de cada elemento se
  validan como los de la entidad (400), y en clean-ddd además en el dominio (422).
- Una lista nunca es opcional (`Tags:string[]?` no vale): sin elementos es una lista vacía.
- En los eventos de integración la lista viaja con sus datos; los elementos de una lista de objetos van como un record
  dentro del evento (`OrderCreated.OrderItem`), así que quien escucha no necesita el tipo del que publica.
- Cambiar un tipo de elemento cambia la tabla de las listas que lo usan: crea una migración nueva. No se puede eliminar
  un tipo mientras alguna lista lo use.
- En el Studio: **Entidades → Tipos de elemento**, y en cada propiedad la columna **Valor** (Obligatorio, Opcional o
  Lista); los tipos de elemento aparecen en el selector como `▤ OrderItem[]`. En MCP: `set_type` y `remove_type`.
- `stratum import openapi` convierte los arrays: de valores, a listas; de objetos, a tipos de elemento.
- En clean-ddd, las operaciones del agregado admiten un elemento o una lista como parámetro, para cambiar la lista con su
  propia regla:

  ```bash
  stratum entity operation add Order AddItem --params "Item:OrderItem"          # evento OrderItemAdded
  stratum entity operation add Order RemoveItem --params "ProductId:guid"         # evento OrderItemRemoved
  stratum entity operation add Order ReplaceItems --params "Items:OrderItem[]"
  ```

  `Order.AddItem.cs` nace ya con `Items.Add(item);` (y `ReplaceItems` con `Items = [.. items];`); `RemoveItem` trae la
  línea de ejemplo comentada. La regla («no se tocan las líneas de un pedido enviado») la añades tú.

### Buenas prácticas y agentes de código

Las plantillas siguen las skills oficiales del equipo de .NET ([dotnet/skills](https://github.com/dotnet/skills):
`dotnet-webapi`, `optimizing-ef-core-queries`, `test-anti-patterns`, `msbuild`), con dos excepciones a propósito: en
vertical slice los endpoints usan el `DbContext` sin capa de servicios, y el `PUT` responde 204.

- **OpenAPI**: cada endpoint lleva nombre, resumen, descripción y sus respuestas (400 de validación incluido); los
  comentarios `<summary>` de los contratos pasan al documento (`GenerateDocumentationFile`).
- **JSON estricto** en los servicios nuevos (`Program.cs`): números como números, nombres de propiedad exactos y sin
  propiedades repetidas.
- **Contratos propios para los elementos de las listas** (`OrderItemContract`): la API nunca expone la clase que guarda EF.
- **`<Servicio>.Api.http`** con las peticiones de cada entidad y operación, encadenadas (el pedido usa el cliente creado
  antes). Stratum lo actualiza mientras no lo edites.
- **Tests enfocados**: crear (201 y `Location`), leer lo creado, que el `PUT` reemplace los datos (con valores distintos y
  los opcionales vacíos, comparando campo a campo), listar y borrar, cada uno por separado.
- **`AGENTS.md`** (y `CLAUDE.md`, que lo importa) en cada servicio nuevo: qué archivos son de Stratum y no se editan, dónde
  va tu código, los comandos y las convenciones, y qué skills de Microsoft instalar. Así Copilot o Claude Code escriben tu
  parte siguiendo las mismas reglas.

Los servicios que ya tenías no cambian de comportamiento: el `.http`, el `AGENTS.md` y el JSON estricto (en
`Program.cs`) solo se crean en servicios nuevos; los endpoints y los tests (`.g.cs`) se actualizan en el siguiente cambio.

### Operaciones del agregado (clean-ddd)

```bash
stratum entity operation add Order Confirm
stratum entity operation add Order Cancel --params "Reason:string"          # evento OrderCancelled
stratum entity operation remove Order Confirm
```

Cada operación genera el evento de dominio (`OrderCancelled(OrderId, Reason)`), el comando `CancelOrder` con su handler y
`POST /api/v1/orders/{id}/cancel`. La regla es tuya: Stratum crea una vez `Order.Cancel.cs` con el método vacío (lanza
`DomainException` para responder 422) y no lo vuelve a tocar. Si la entidad publica eventos, la operación publica
`OrderUpdated` con el estado nuevo. En el Studio: **Entidades → Operaciones del agregado**.

#### Publicar una operación para otros servicios

```bash
stratum entity operation add Order Ship --params "Carrier:string" --publish   # o: stratum entity operation publish Order Ship
stratum subscribe ../orders Order.Shipped --path ../notifications            # en el servicio que lo escucha
```

Con `--publish`, el evento de la operación es también un **evento de integración propio**: `OrderShipped` en el topic
`orders.order-shipped`, con el id, los datos del pedido después de la operación y los parámetros (`Carrier`). Se publica
por el outbox junto con `OrderUpdated` (si la entidad publica sus cambios), así que quien guarda una copia del pedido
sigue al día y quien tiene que *reaccionar al envío* escucha solo `OrderShipped`, una vez por envío. Aparece en el mapa
de eventos, en AsyncAPI y en los topics del AppHost (Service Bus incluido). Para publicarse, el evento tiene que empezar
por el nombre de la entidad. En el Studio: columna **Publica** de las operaciones; en MCP: `add_operation` con
`publish` o `publish_operation`.

### El servicio como servidor MCP (feature `mcp`)

```bash
stratum feature on mcp            # o: stratum new Orders --mcp
```

El servicio publica `/mcp` (Streamable HTTP, con [ModelContextProtocol.AspNetCore](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore)):
cada entidad tiene `list_orders`, `get_order`, `create_order`, `update_order` y `delete_order`, y cada operación del
agregado la suya (`cancel_order`). Las herramientas llaman a los mismos métodos que los endpoints HTTP, así que tienen la
misma validación, las mismas comprobaciones de referencias, reglas de negocio, eventos y autenticación (con `auth`, el
cliente MCP envía el mismo token). Un test generado (`McpTests.g.cs`) comprueba el handshake y la lista de herramientas.

## Importar una especificación OpenAPI

```bash
stratum import openapi orders.yaml --dry-run                 # qué saldría, sin escribir nada
stratum import openapi orders.yaml -a clean-ddd -o orders    # servicio nuevo (acepta las opciones de 'stratum new')
stratum import openapi https://…/openapi.json --path billing # entidades nuevas en un servicio existente
```

OpenAPI 3.x o Swagger 2.0, en JSON o YAML:

- Cada recurso CRUD (`/orders` y `/orders/{id}`, con o sin prefijo `/api/v1`) es una entidad, con las propiedades de su
  esquema (`allOf` incluido; `snake_case` pasa a PascalCase; `id`, `createdAt` y `updatedAt` ya los genera Stratum).
- Una propiedad que apunta a otro recurso (`$ref` o `customer_id` con formato uuid) es una referencia.
- `POST /orders/{id}/cancel` es la operación `Cancel` del agregado en clean-ddd (con los datos del cuerpo como parámetros).
- Los arrays pasan a [listas](#listas): de valores, o de objetos con su tipo de elemento.
- Al final, la lista de lo que no se ha podido traducir (objetos anidados fuera de una lista, listas de listas…) y de los endpoints que no son CRUD,
  para que sepas qué escribir a mano.
- En el Studio: **Servicios → Crear un servicio desde OpenAPI** rellena el servicio nuevo para que lo revises antes de aplicar.
- En un asistente de IA: la herramienta `import_openapi` del [servidor MCP](#asistentes-de-ia-stratum-mcp).

## Features en un servicio existente (`stratum feature`)

```bash
stratum feature list
stratum feature off docker openApi --dry-run
stratum feature on auth
stratum feature convert --dry-run     # Program.cs de antes de la 0.12
```

Desde la 0.12, `Program.cs` solo llama a `builder.AddStratumWiring()` y `app.UseStratumWiring()`; lo que activa cada feature
(Stratum.ServiceDefaults.*, OpenAPI + Scalar, auth, validación, entidades) está en `StratumWiring.g.cs`, que Stratum regenera.
Tus servicios y middleware van en `Program.cs`, después de esas llamadas.

Al activar o desactivar una feature, Stratum:

- regenera `StratumWiring.g.cs` y el resto de `.g.cs`;
- añade o quita sus paquetes en `.csproj` y `Directory.Packages.props` (solo los que puso él; los tuyos no los toca);
- crea sus archivos (Dockerfile, workflow de CI, proyecto de tests…) y, al desactivarla, los borra **solo si siguen como él los
  generó**; si los cambiaste, te avisa en lugar de borrarlos o actualizarlos.

En el Studio, los interruptores de Features funcionan también en servicios existentes, con el mismo plan y diff antes de aplicar.

### `.g.cs` editados a mano

Cada `.g.cs` termina con `// stratum-hash: …`. Si lo editas a mano, Stratum lo detecta y te avisa antes de regenerarlo (y marca el
archivo como tuyo en la revisión): pasa ese cambio a la clase `partial` o a las plantillas del equipo.

## Migraciones guiadas

Después de cambiar entidades, la migración de EF Core tiene que reflejarlo sin perder datos:

```bash
dotnet ef migrations add RenameTitle --project src/Shop.Api      # clean-ddd: --project src/X.Infrastructure --startup-project src/X.Api
stratum migration review --dry-run                                   # qué cambiaría y qué borra datos
stratum migration review                                             # corrige la migración y vacía pendingRenames
```

- Los renombrados de `stratum.json` se convierten en `RenameColumn` (EF Core generaría `DropColumn` + `AddColumn` y se perderían
  los datos). Si además cambia el tipo, se añade un `AlterColumn` y se avisa: fallará si la base de datos no puede convertir los valores.
- Avisa de todo lo que borra datos (`DropTable`, `DropColumn`) y de los cambios que pueden fallar con datos existentes
  (cambio de tipo, columna que pasa a obligatoria, longitud máxima menor).
- En el Studio (página del servicio, sección Migraciones) es un solo paso: **Comprobar** dice si las entidades tienen cambios sin
  migración, **Crear y revisar migración** la crea y la corrige, **Ver SQL** enseña el script y **Deshacer esta migración**
  la quita (y devuelve sus renombrados a `stratum.json`) mientras no se haya aplicado.

## Eliminar un servicio

```bash
stratum service remove orders                 # enseña todo lo que se eliminaría; no toca nada
stratum service remove orders --yes           # lo elimina
stratum service remove D:/apis/payments --path C:/servicios --keep-files --yes   # solo lo saca del sistema
```

Se elimina el servicio y lo que cuelga de él:

- su carpeta (código, tests, migraciones, `stratum.json`). Con `--keep-files` (solo para servicios de otras carpetas) se conserva;
- las suscripciones de otros servicios a sus eventos: sus contratos y los handlers que no hayas tocado (de los que sí, avisa);
- su entrada en `stratum.workspace.json` y sus referencias en el AppHost y la solución, que se regeneran.

La base de datos y los topics de Kafka viven en los contenedores de Aspire: Stratum te dice cuáles son para que los borres
si ya no los necesitas. En el Studio: **General → Zona peligrosa → Eliminar**, con el mismo resumen y confirmación escribiendo
el nombre del servicio.

Al quitar una entidad o dejar de publicar sus eventos, los servicios que los escuchaban aparecen marcados en el mapa de
eventos; `stratum subscribe --sync --prune` (o *Quitar* en su Mensajería) limpia esas suscripciones.

## Servicios en otras carpetas (`stratum.workspace.json`)

Un sistema es una carpeta con servicios dentro, pero un servicio puede vivir en otra ruta (otro disco, otro repositorio):

```bash
stratum new Payments -o D:/apis/payments
stratum workspace add D:/apis/payments --path C:/servicios   # lo apunta en C:/servicios/stratum.workspace.json
stratum workspace list --path C:/servicios
stratum aspire --path C:/servicios                            # el AppHost lo incluye
```

En el Studio, **Nuevo servicio → Ubicación** (con un explorador de carpetas) y **Añadir un servicio de otra carpeta** hacen lo mismo.

## Mensajería entre servicios (Kafka o Azure Service Bus)

```bash
stratum messaging on --broker kafka        # o azureServiceBus, o inMemory (solo dentro del proceso)
stratum events on Product                  # publica ProductCreated / ProductUpdated / ProductDeleted
stratum aspire                             # añade Kafka y Kafka UI, o el emulador de Service Bus, al AppHost
```

En el Studio: **Servicio → Mensajería entre servicios** (broker) y **Entidades → Eventos para otros servicios** (qué entidad publica).

**No hace falta activar la mensajería antes.** Publicar eventos (`events on`, `--publish` en una operación, la casilla
**Publica**) o escuchar uno (`subscribe`, **+ Escuchar**, arrastrar en el mapa) la activa si el servicio no la tenía, con
el broker que usa el resto del sistema (Kafka si no hay otro), y lo avisa. `messaging on` sigue sirviendo para elegir
otro broker.

Al crear un servicio en el Studio eliges su **carpeta** dentro del sistema: navegas por las carpetas (las que ya son
servicios aparecen marcadas y no se pueden usar), y con **+ Nueva carpeta** creas las que quieras (`backend`,
`backend/ventas`…). Stratum encuentra los servicios hasta 5 niveles por debajo de la carpeta del sistema, y el AppHost
los incluye a todos. Una carpeta fuera del sistema sigue siendo posible («Usar una carpeta fuera del sistema…»). Por
consola es lo mismo con `-o`: `stratum new Sales -o backend/ventas/sales`.

**Abrir el código y compilar desde el Studio.** Abajo a la izquierda eliges tu editor (Visual Studio, VS Code o Rider, si
están instalados, o la aplicación predeterminada). Con él:

- **Abrir en Visual Studio** (barra superior): la solución del servicio; si hay AppHost, la del sistema, con todos los
  servicios. En **Ejecutar → Código de todo el sistema**, la solución del sistema.
- **Abrir el archivo que toca editar**: la regla de cada operación (`Order.Ship.cs`, en Entidades → Comportamiento), el
  handler de cada evento escuchado (en Mensajería) y cualquier archivo de **Código generado**.
- **Compilar y probar** (menú del servicio): `dotnet build` y `dotnet test` con la salida en directo y un resumen: tests
  que pasan y fallan (con el motivo) y errores de compilación con archivo y línea, que se abren en el editor con un clic.
  En **Ejecutar**, lo mismo para todo el sistema.

El Studio se entera de los cambios hechos fuera (consola, asistente de IA, editor): recarga el servicio abierto, o, si
tienes cambios sin aplicar, te avisa y te deja elegir.

Qué genera:

- `StratumWiring.g.cs`: `AddStratumMessaging("shop").UseKafka(...).UseEntityFramework<ShopDbContext>()`.
- Tablas `msg_outbox` y `msg_inbox` (`MessagingConfiguration.g.cs`): crea la migración después de activar la mensajería.
- `IntegrationEvents/ProductIntegrationEvents.g.cs`: el contrato público, con topic `shop.product-created`, etc.
  - Vertical slice: los endpoints publican con `IEventBus` en la misma transacción que el cambio.
  - Clean + DDD: `ProductIntegrationEventsMapper.g.cs` convierte los eventos de dominio en eventos de integración al guardar.
- Tests: el broker se sustituye por uno en memoria (`ApiFactory.g.cs`), no hace falta Kafka.
- AppHost: `AddKafka("kafka").WithKafkaUI()` y `WithReference(kafka).WaitFor(kafka)` en cada servicio con Kafka.
- AppHost con Azure Service Bus: `AddAzureServiceBus("servicebus").RunAsEmulator()`, un topic por evento publicado y una
  suscripción por servicio que lo escucha (el emulador no crea entidades al vuelo, por eso las declara el AppHost). En Azure
  real usa la misma cadena `ConnectionStrings:servicebus`, o `Messaging:AzureServiceBus:CreateEntities=true` para que el
  servicio cree topics y suscripciones al arrancar.
- Los servicios que se hablan tienen que usar el mismo broker: `stratum events map` avisa si no.

### Escuchar eventos de otro servicio

```bash
stratum subscribe ../orders Order.Created --path billing   # o "Order" para Created, Updated y Deleted
stratum unsubscribe Orders Order.Created --path billing
stratum subscribe --sync --path billing                    # si Orders cambió los datos del evento
stratum events map                                         # quién publica y quién escucha cada evento
```

- Stratum copia el contrato en `IntegrationEvents/Orders/OrderCreated.g.cs` (en `stratum.json` queda una copia de sus datos, así que
  el servicio se genera aunque `orders` esté en otro repositorio) y crea `Events/Orders/OrderCreatedHandler.cs`, que es tuyo.
- El handler se ejecuta una vez por evento (inbox), con reintentos y dead-letter; lo que guardes en él se confirma junto con
  el registro del inbox.
- En el Studio: **Servicio → Eventos que escucha** (elegir de los eventos que publican los demás servicios, actualizar el
  contrato cuando cambia) y **Sistema → Mapa de eventos**.

#### Mapa de eventos (Studio)

Un resumen compacto del sistema, de solo lectura: una ficha pequeña por servicio (broker, cuántos eventos publica y escucha,
avisos) y una tabla con cada evento, quién lo publica, quién lo escucha y si llega (en rojo si hay un problema: contrato
desactualizado, nadie lo publica, brokers distintos). Desde ahí, **Abrir diseñador** para cambiar las conexiones y
**Especificación AsyncAPI**.

#### Diseñador de eventos (Studio)

Cuando el sistema crece, **Mapa de eventos → Abrir diseñador** abre una ventana aparte (`/designer`) con un lienzo:

- **Paleta de servicios** a la izquierda: arrastra un servicio al lienzo para trabajar con él; **✕** en su tarjeta lo quita
  del lienzo (no borra nada). **+ Nuevo servicio…** y la papelera de cada tarjeta llevan al Studio (crear o eliminar un
  servicio, con su plan); al volver, el diseñador se actualiza.
- **Lienzo**: mueve las tarjetas por la cabecera, desplázate arrastrando el fondo y haz zoom con la rueda o con **−/+**;
  **Ajustar** encuadra todo. Las posiciones se guardan en `stratum.designer.json`, en la carpeta del sistema.
- **Conectar**: arrastra un evento hasta otro servicio. Cada entidad enseña sus eventos *posibles* en gris
  (`Created/Updated/Deleted` y sus operaciones): si conectas uno gris, se publica en el mismo paso (solo ese evento:
  `publishedEvents` en el `stratum.json`), y la mensajería se activa donde haga falta.
- **Desconectar**: pulsa una flecha (o una fila de *Escucha*) y **Desconectar**, la tecla **Supr**, o **×** en la fila.
  Tu handler se conserva.
- **Publicar / dejar de publicar**: pulsa un evento. Si alguien lo escucha, te avisa.

Los cambios se acumulan en **Cambios sin guardar** (cada uno con **↶** para deshacerlo) y debajo ves el plan de cada servicio
afectado. **Guardar y cerrar** aplica todo a la vez y cierra la ventana; si algún cambio no es posible, no se escribe nada.
**Descartar** cierra sin tocar nada. El Studio recoge los cambios solo.

Paquetes:

| Paquete | Qué hace |
| --- | --- |
| `Stratum.Messaging` | `IEventBus` (outbox), handlers `IIntegrationEventHandler<T>` que se ejecutan una vez por evento (inbox), reintentos con espera creciente y dead-letter, trazas de extremo a extremo y transporte en memoria. Sin dependencias de terceros. |
| `Stratum.Messaging.EntityFrameworkCore` | Outbox e inbox en la base de datos del servicio: el evento se guarda en la misma transacción que tus cambios, y el handler y su registro en el inbox también. |
| `Stratum.Messaging.Kafka` | Kafka con Confluent.Kafka: un topic por evento, un consumer group por servicio, commit tras procesar, topic `<topic>.dead-letter`, topics creados al primer uso. |
| `Stratum.Messaging.AzureServiceBus` | Azure Service Bus: un topic por evento, una suscripción por servicio, complete tras procesar y dead-letter nativo; funciona con el emulador de Aspire. |

### AsyncAPI

```bash
stratum events asyncapi                    # asyncapi.yaml en la carpeta del sistema (-o eventos.json para JSON)
```

Especificación AsyncAPI 3.1 de los eventos del sistema, sacada de los `stratum.json` como el mapa de eventos: un channel por
topic, la operación `send` del servicio que lo publica y una `receive` por cada servicio que lo escucha, el esquema de los
datos tal como viajan (camelCase) y las cabeceras de `Stratum.Messaging`. Sirve para AsyncAPI Studio, generadores de
clientes o para que otros equipos sepan qué pueden escuchar. En el Studio: **Mapa de eventos → Especificación AsyncAPI**.

### `Idempotency-Key` en la API

Todos los servicios lo tienen (`Stratum.ServiceDefaults`): un cliente que manda `POST`/`PUT`/`PATCH`/`DELETE` con la cabecera
`Idempotency-Key` puede reintentar sin miedo (timeout, doble clic). La primera petición se ejecuta y su respuesta se guarda;
los reintentos con la misma clave reciben la misma respuesta con `Idempotent-Replayed: true` sin volver a ejecutarse.

- La misma clave con otra petición → 422. Un reintento mientras la primera sigue en curso → 409.
- Solo se recuerdan las respuestas correctas: tras un 400 o un 500 el reintento se ejecuta de nuevo.
- Las claves son por usuario (cabecera `Authorization`) y duran 24 h (`Idempotency:Expiration`).
- Se guardan en `IDistributedCache`, en memoria por defecto; con varias instancias registra Redis
  (`AddStackExchangeRedisCache`) para compartirlas.
- Sin cabecera, nada cambia.

## Asistentes de IA: `stratum mcp`

Stratum es también un servidor [MCP](https://modelcontextprotocol.io): Claude, GitHub Copilot o Cursor cambian tus
servicios a través de Stratum, en lugar de escribir a mano código que luego se regenera.

```bash
stratum mcp config            # configuración lista para pegar en Claude Desktop, Claude Code, VS Code y Cursor
```

- **Herramientas**: `list_services`, `get_service`, `event_map`, `get_asyncapi`, `create_service`, `add_entity`,
  `update_entity`, `remove_entity`, `import_openapi`, `set_enum`, `remove_enum`, `add_operation`, `remove_operation`, `set_features`, `set_messaging`, `set_event_publishing`, `subscribe`, `unsubscribe`,
  `sync_subscriptions`, `review_migration`, `generate_apphost`, `remove_service`.
- **Plan primero**: las que cambian algo devuelven el plan (lo mismo que `--dry-run`) y solo escriben con
  `"apply": true`, que el asistente envía cuando tú apruebas el plan. Cada herramienta ejecuta el comando de la CLI
  correspondiente: el resultado es el mismo que desde la CLI o el Studio, con la misma protección de tus archivos.
- **Recursos**: el `stratum.json` de cada servicio y `asyncapi.yaml`.
- **Prompts**: `design_from_spec` (le pasas requisitos o historias de usuario y propone servicios, entidades y eventos;
  los crea cuando lo apruebas) y `review_system` (busca problemas sin cambiar nada).
- Protocolo: stdio, compatible con clientes de la revisión 2026-07-28 y anteriores (handshake `initialize`).
- Mientras lo ejecutas desde el código fuente, un cliente MCP abierto bloquea `stratum.dll` en Windows: ciérralo antes
  de recompilar.

## Qué genera

```
Orders/
├── stratum.json                     ← definición del servicio (la fuente de verdad)
├── Orders.slnx
├── Directory.Build.props        ← net10.0, nullable, warnings = errores, analyzers
├── Directory.Packages.props     ← Central Package Management
├── global.json  .editorconfig  .gitignore
├── Dockerfile  .dockerignore    ← multi-stage, usuario no-root
├── .github/workflows/ci.yml
├── src/Orders.Api/              ← lo propio del servicio: endpoints, opciones
└── tests/Orders.Api.Tests/      ← tests del contrato común
```

## Paquetes `Stratum.ServiceDefaults.*`

Uno por feature, para que quien no use una no arrastre sus dependencias:

| Paquete | Qué aporta | Dependencias |
| --- | --- | --- |
| `Stratum.Ddd` | `AggregateRoot`, `DomainEvent`, `DomainException`, `ICommand`/`IQuery` y sus handlers | Ninguna |
| `Stratum.ServiceDefaults` | `AddStratumServiceDefaults()`, `UseStratumServiceDefaults()`, `MapStratumDefaultEndpoints()`, `AddValidatedOptions<T>()`, `AddStratumApplication()` | Solo ASP.NET Core + `Stratum.Ddd` |
| `Stratum.ServiceDefaults.OpenTelemetry` | `AddStratumOpenTelemetry("servicio")` | OpenTelemetry |
| `Stratum.ServiceDefaults.Resilience` | `AddStratumResilience()`, `AddStratumServiceDiscovery()` | Microsoft.Extensions.Http.Resilience, Microsoft.Extensions.ServiceDiscovery |
| `Stratum.ServiceDefaults.Authentication` | `AddStratumAuthentication()` (sección `Auth`) | JwtBearer |

Todos comparten versión con Stratum (`<Version>` en `Directory.Build.props`). Las versiones de sus dependencias viven en `Directory.Packages.props` de este repo; un servicio generado solo fija `Stratum.*`.

**Flujo de una actualización de seguridad:** subir la dependencia aquí → nueva versión de Stratum → en cada servicio, `stratum upgrade` → PR.

| Siempre incluido | Opcional (`--no-x` / `--x`) |
| --- | --- |
| Health checks `/health/live` y `/health/ready` | `observability`: OpenTelemetry (trazas, métricas, logs) |
| Errores `application/problem+json` con `traceId` y `correlationId` | `openApi`: documento OpenAPI + Scalar en `/scalar` (solo Development) |
| `X-Correlation-ID` en respuesta y en cada log | `resilience`: retry/timeout/circuit breaker en todo `HttpClient` |
| Logs JSON en producción y legibles en desarrollo | `auth`: JWT bearer, autenticado por defecto (**off** por defecto) |
| Configuración validada al arrancar (`AddValidatedOptions<T>`) | `tests`, `docker`, `ci` |
| API versionada (`/api/v1`) y puerto local estable por servicio | `mcp`: servidor MCP en `/mcp` con las entidades (**off** por defecto) |
| `Idempotency-Key` en POST/PUT/PATCH/DELETE | |

## Cómo funciona

```
stratum.json ──► ServiceDefinition ──► ServiceGenerator (puro, en memoria) ──► FileWriter (CLI)
                                         │                                └─► zip / repo (editor visual, futuro)
                                         └── templates/service/** (recursos incrustados)
```

- **`src/Stratum.Core`**: modelo, motor de plantillas y generador. No escribe en disco, así que el editor visual podrá reutilizarlo tal cual.
- **`src/Stratum.Cli`**: la CLI (`new`, `add entity`, `aspire`, `studio`, `upgrade`). Sin dependencias externas: solo .NET y ASP.NET Core.
- **`src/Stratum.Studio`**: el editor visual (componentes Blazor y estado), sobre `Stratum.Core`.
- **`src/Stratum.ServiceDefaults*`**: los paquetes que consumen los servicios generados, con sus tests en `tests/`.
- **`templates/service`**: plantillas como archivos reales (C#, XML, YAML, JSON), fáciles de leer y editar:
  - `{{Service}}`, `{{service-kebab}}`, `{{DevPort}}` y `{{StratumVersion}}` en el contenido; `__Service__` en las rutas.
  - Líneas con `@stratum:if feature`, `@stratum:if !feature`, `@stratum:else` y `@stratum:end` dentro de cualquier comentario. La línea de la directiva se elimina del resultado.
  - Un archivo cuya plantilla queda vacía no se genera.
  - `_gitignore` → `.gitignore` y `_github/` → `.github/`, para que no haya archivos ocultos en las plantillas.

## Desarrollo

```bash
dotnet build Stratum.slnx
dotnet test Stratum.slnx

# Mientras los paquetes no estén en nuget.org: empaqueta en local y apunta el servicio a esa carpeta.
dotnet pack Stratum.slnx -c Release -o ./artifacts/packages
dotnet run --project src/Stratum.Cli -- new Orders -o ../Orders --package-source ./artifacts/packages
```

`--package-source` genera en el servicio un `nuget.config` con *package source mapping*: `Stratum.*` sale solo de esa carpeta y todo lo demás solo de nuget.org. Si reempaquetas la misma versión, borra la caché: `dotnet nuget locals global-packages --clear` (el smoke test ya lo hace solo para `Stratum.*`).

Test de extremo a extremo (genera, compila, arranca y prueba los endpoints):

```powershell
# Windows
powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
```

```bash
# Linux / macOS / Git Bash
./scripts/smoke-test.sh
```

Para instalar `msg` como herramienta global:

```bash
dotnet pack src/Stratum.Cli -c Release -o ./artifacts/packages
dotnet tool install -g Stratum.Cli --add-source ./artifacts/packages
```

## Roadmap

1. ✅ **Fase 1**: CLI + servicio con la base común.
2. **Fase 2**
   - ✅ 2a: lo común en paquetes `Stratum.ServiceDefaults.*` + `stratum upgrade`.
   - ✅ 2b: `stratum add entity` con CRUD, EF Core (PostgreSQL o SQL Server) y tests con SQLite en memoria.
   - 2c: publicar los paquetes en nuget.org desde CI al crear un tag.
3. ✅ `stratum aspire`: AppHost de Aspire para todos los servicios.
4. ✅ **Fase 3**: editor visual `stratum studio` (Blazor Server sobre `Stratum.Core`).
5. ✅ Entidades editables: cambiar, renombrar y eliminar propiedades y entidades sin pisar tu código (`stratum entity`, Studio).
6. ✅ Migraciones guiadas: renombrados sin perder datos, avisos de lo que borra datos, SQL y deshacer (`stratum migration review`, Studio).
7. ✅ Features editables en servicios existentes (`stratum feature`, Studio), paquetes y archivos que sobran se quitan, `.g.cs` editados a mano detectados.
8. En curso: mensajería entre servicios. ✅ `Stratum.Messaging`, outbox/inbox con EF Core, Kafka con Aspire, publicar eventos de entidades (CLI y Studio). ✅ Suscripciones y mapa de eventos. ✅ Azure Service Bus e `Idempotency-Key`.
9. En curso: ✅ AsyncAPI, ✅ servidor MCP, ✅ referencias entre entidades y operaciones del agregado, ✅ importar OpenAPI, ✅ feature `mcp` en los servicios.
10. ✅ Nombres de Stratum: paquetes `Stratum.*`, `stratum.json`, `.stratum`, con migración automática (`stratum upgrade`).
11. ✅ Enumerados (`stratum enum`, Studio, MCP, import de OpenAPI). ✅ Mapa de eventos gráfico y editable en el Studio.
12. ✅ Listas: de valores (`Tags:string[]`) y de objetos con tipos de elemento (`Items:OrderItem[]`), en CLI, Studio, MCP, eventos e import de OpenAPI. ✅ Operaciones sobre listas (`AddItem`, `RemoveItem`).
13. ✅ Buenas prácticas de dotnet/skills en las plantillas, `AGENTS.md`, `.http`, siguientes pasos con Aspire y mejoras del Studio.
14. ✅ Operaciones publicadas como eventos de integración propios (`OrderShipped`).
15. ✅ Sin dependencias de orden: la mensajería se activa al publicar o escuchar; el Studio recarga los cambios externos.
16. ✅ Carpetas para los servicios dentro del sistema; abrir en Visual Studio / VS Code / Rider; compilar y probar desde el Studio.
17. ✅ Diseñador de eventos en una ventana aparte: lienzo con paleta de servicios, conectar y desconectar arrastrando, publicar eventos sueltos.
18. ✅ Ventanas flotantes en Entidades para propiedades, operaciones, enumerados y tipos de elemento.
19. Publicar en nuget.org (prefijo `Stratum.*`, la herramienta como `Stratum.Cli`).
4. **Fase 4**: varios servicios conectados (eventos, Aspire AppHost, docker-compose).
