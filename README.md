# Stratum: generador de microservicios .NET

Stratum genera microservicios .NET 10 desde la línea de comandos o desde un editor visual (`stratum studio`) que nacen con lo que cualquier equipo serio exige siempre igual: estructura, observabilidad, errores, configuración validada, tests, Docker y CI. Lo común no se copia en cada servicio: llega en los paquetes `Msg.ServiceDefaults.*`, así que mejorarlo o parchearlo es subir una versión. El objetivo final es un editor visual.

> **Nombre:** la herramienta se llamaba MSG y ahora es **Stratum** (comando `stratum`). Los paquetes `Msg.*`, el archivo
> `msg.json` y la carpeta `.msg` mantienen su nombre por ahora; se renombrarán antes de publicar en NuGet.

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
stratum new --from msg.json        # regenera desde una definición
stratum new Orders --dry-run       # lista los archivos sin escribir nada
stratum upgrade                    # actualiza los paquetes Msg.* de un servicio a la versión de Stratum instalada

# Dentro de un servicio:
stratum add entity Product --props "Name:string, Price:decimal, Description:string?"
stratum add entity Invoice --props "Number:string, Total:decimal" --db sqlserver   # la primera entidad elige la BD
```

## Arquitecturas

| | `vertical-slice` (por defecto) | `clean-ddd` |
| --- | --- | --- |
| Proyectos | `Api` | `Domain`, `Application`, `Infrastructure`, `Api` |
| Entidades | Clases con propiedades | Agregados (`AggregateRoot<Guid>`) con `Create`/`Update`/`Delete`, reglas en `Apply` y `DomainException` → 422 |
| Lógica | En el endpoint, con el `DbContext` | Commands y queries con sus handlers, que el endpoint recibe por DI (sin mediator) |
| Eventos | No hay | `XCreated`/`XUpdated`/`XDeleted`, publicados tras el commit por un interceptor de EF Core |
| Tests | Integración | Integración + tests de dominio sin base de datos |

Los bloques de construcción de DDD están en el paquete `Msg.Ddd`, que no tiene dependencias, y los puede referenciar el proyecto Domain.
No usamos MediatR: desde la v13 tiene licencia dual (RPL-1.5 o comercial). Los handlers se registran con `AddMsgApplication()`.

## `stratum studio`

```bash
stratum studio                       # en la carpeta que contiene tus servicios (o --path <carpeta>)
```

Abre el editor visual en `http://localhost:5150`. Usa el mismo motor que la CLI, así que lo que ves es lo que se genera:

- **Sistema**: tus servicios, crear uno nuevo y generar o actualizar el AppHost de Aspire.
- **Servicio**: arquitectura, features y base de datos, con vista previa en vivo de `Program.cs`, el árbol de archivos y `msg.json`.
- **Entidades**: propiedades con tipo y opcionalidad. En Clean + DDD muestra los métodos del agregado y sus eventos. A la derecha, el código generado de cada capa.
- **Aplicar cambios**: qué archivos se crean, cuáles se regeneran y cuáles se completan, con el diff de cada uno. Nunca se sobrescribe un archivo tuyo.
- **Ejecutar sistema** (en Sistema): genera o actualiza el AppHost y lo arranca con Aspire. Muestra la salida en vivo, el enlace al
  dashboard y los de Scalar y health de cada servicio. Avisa si falta Docker; al cerrar el Studio se detiene todo.
- **Migraciones** (en Servicio): lista las migraciones y crea la siguiente con un nombre sugerido (`Initial`, `AddProduct`…),
  con los proyectos correctos según la arquitectura. Si falta `dotnet-ef`, ofrece instalarlo.

En un servicio que ya existe solo se pueden añadir entidades: las existentes, la arquitectura y las features ya son código tuyo.
Opciones: `--port`, `--no-browser` y `--package-source` (igual que en `stratum new`).

## Plantillas del equipo (`.msg`)

Stratum trae sus convenciones, pero las que mandan son las de tu equipo. Pon una carpeta `.msg` en la raíz de tu repo
(o de tu carpeta de servicios) y haz commit. `stratum new`, `stratum add entity` y `stratum studio` la encuentran subiendo de carpeta:

```bash
stratum templates list                                            # plantillas de Stratum y del equipo
stratum templates eject service/src/__Service__.Api/Program.cs     # copia una a .msg/templates para editarla
stratum templates eject entity-clean                               # o un set entero
```

| En `.msg/templates/<set>/…` | Efecto |
| --- | --- |
| Misma ruta que una plantilla de Stratum | La sustituye |
| Ruta nueva (p. ej. `service/CODEOWNERS`) | Se añade a lo que genera Stratum |
| Archivo vacío | Esa plantilla no se genera (p. ej. `service/Dockerfile`) |

Sets: `service`, `entity` (vertical slice), `entity-clean` y `entity-tests`. Las plantillas usan los tokens y las
condiciones de Stratum (`{{Service}}`, `{{Entity}}`, `@msg:if auth`…). Además, `.msg/config.json` define tokens propios:

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
- La telemetría aparece sola en el dashboard: Aspire pasa `OTEL_EXPORTER_OTLP_ENDPOINT` y `AddMsgOpenTelemetry()` la usa.
- Enlace directo a Scalar en el dashboard para cada servicio con OpenAPI.
- Service discovery: en `AppHost.cs`, `services.Billing.WithReference(services.Orders)` y Billing llama a `http://orders`.
- `MsgServices.g.cs` y la `.slnx` se regeneran en cada `stratum aspire`; `AppHost.cs` es tuyo. Volver a ejecutarlo incorpora los servicios nuevos.

AppHost en Aspire 13.6.5 (la línea validada con .NET 10). Para pasar a Aspire 17: `aspire update`.

## `stratum add entity`

Por cada entidad genera la entidad (Id UUID v7, `CreatedAt`, `UpdatedAt`), su `IEntityTypeConfiguration`, los DTOs con validación
(400 ProblemDetails gracias a la validación integrada de .NET 10), los endpoints CRUD paginados y sus tests. La primera entidad añade
además el `DbContext`, el health check de base de datos (readiness), `compose.yaml` y una base SQLite en memoria para los tests.

Tipos de `--props`: `string`, `int`, `long`, `decimal`, `bool`, `guid`, `date`, `datetime`. Con `?` la propiedad es opcional.

Reglas para no pisar tu código:

| Archivos | Qué hace Stratum |
| --- | --- |
| `*.g.cs`: propiedades, configuración EF, contratos, CRUD, comandos/queries, tests CRUD, registros, `DbSet`s | Los regenera siempre a partir de `msg.json`. |
| Clases `partial` sin `.g` (`Product.cs`, `ProductEndpoints.cs`), handlers de eventos, repositorio, `DbContext`, `compose.yaml` | Los crea una vez. A partir de ahí son tuyos. |
| `.csproj`, `Directory.Packages.props`, `appsettings.Development.json` | Solo añade lo que falta. |
| Todo lo demás | No lo toca. |

Tu código va en las clases `partial`: métodos y propiedades calculadas en la entidad, reglas en `EnsureInvariants()` (clean-ddd),
mapeo EF extra en `Customize(builder)` y endpoints propios en `MapCustomEndpoints(group)`.

La entidad queda en `msg.json`, así que `stratum new --from msg.json` reconstruye el servicio con todas sus entidades.

## Cambiar entidades (`stratum entity`)

```bash
stratum entity list
stratum entity update Product --props "Title:string, Price:decimal, Stock:int" --rename Name=Title --dry-run
stratum entity remove Tag                      # tus archivos de Tag se quedan (y te los lista)
stratum entity remove Tag --delete-own-files   # también los borra
stratum entity convert Product                 # entidades creadas antes de Stratum 0.10
```

- `--props` es la lista completa: lo que no esté se elimina. `--rename Viejo=Nuevo` marca un renombrado; queda en `pendingRenames`
  de `msg.json` para que la migración conserve los datos (`RenameColumn` en lugar de borrar y crear).
- Si tu código usa una propiedad eliminada o renombrada, Stratum te dice en qué archivos.
- En el Studio es lo mismo: cambia el nombre en la fila para renombrar, borra filas, añade otras o elimina la entidad; la pantalla
  de revisión enseña el diff y cuántos archivos tuyos se tocan (solo al convertir o con "borrar también mis archivos").
- Las entidades creadas antes de la 0.10 tenían todo en archivos tuyos: Stratum no las edita hasta que las conviertes. La conversión
  reemplaza la entidad y los endpoints por clases `partial` vacías y borra contratos, configuración, comandos, queries y tests
  (pasan a `.g.cs`); eventos, handlers y repositorio se quedan como están. Revisa el `--dry-run` y pasa tus cambios a las partial.

## Features en un servicio existente (`stratum feature`)

```bash
stratum feature list
stratum feature off docker openApi --dry-run
stratum feature on auth
stratum feature convert --dry-run     # Program.cs de antes de la 0.12
```

Desde la 0.12, `Program.cs` solo llama a `builder.AddMsgWiring()` y `app.UseMsgWiring()`; lo que activa cada feature
(Msg.ServiceDefaults.*, OpenAPI + Scalar, auth, validación, entidades) está en `MsgWiring.g.cs`, que Stratum regenera.
Tus servicios y middleware van en `Program.cs`, después de esas llamadas.

Al activar o desactivar una feature, Stratum:

- regenera `MsgWiring.g.cs` y el resto de `.g.cs`;
- añade o quita sus paquetes en `.csproj` y `Directory.Packages.props` (solo los que puso él; los tuyos no los toca);
- crea sus archivos (Dockerfile, workflow de CI, proyecto de tests…) y, al desactivarla, los borra **solo si siguen como él los
  generó**; si los cambiaste, te avisa en lugar de borrarlos o actualizarlos.

En el Studio, los interruptores de Features funcionan también en servicios existentes, con el mismo plan y diff antes de aplicar.

### `.g.cs` editados a mano

Cada `.g.cs` termina con `// msg-hash: …`. Si lo editas a mano, Stratum lo detecta y te avisa antes de regenerarlo (y marca el
archivo como tuyo en la revisión): pasa ese cambio a la clase `partial` o a las plantillas del equipo.

## Migraciones guiadas

Después de cambiar entidades, la migración de EF Core tiene que reflejarlo sin perder datos:

```bash
dotnet ef migrations add RenameTitle --project src/Shop.Api      # clean-ddd: --project src/X.Infrastructure --startup-project src/X.Api
stratum migration review --dry-run                                   # qué cambiaría y qué borra datos
stratum migration review                                             # corrige la migración y vacía pendingRenames
```

- Los renombrados de `msg.json` se convierten en `RenameColumn` (EF Core generaría `DropColumn` + `AddColumn` y se perderían
  los datos). Si además cambia el tipo, se añade un `AlterColumn` y se avisa: fallará si la base de datos no puede convertir los valores.
- Avisa de todo lo que borra datos (`DropTable`, `DropColumn`) y de los cambios que pueden fallar con datos existentes
  (cambio de tipo, columna que pasa a obligatoria, longitud máxima menor).
- En el Studio (página del servicio, sección Migraciones) es un solo paso: **Comprobar** dice si las entidades tienen cambios sin
  migración, **Crear y revisar migración** la crea y la corrige, **Ver SQL** enseña el script y **Deshacer esta migración**
  la quita (y devuelve sus renombrados a `msg.json`) mientras no se haya aplicado.

## Eliminar un servicio

```bash
stratum service remove orders                 # enseña todo lo que se eliminaría; no toca nada
stratum service remove orders --yes           # lo elimina
stratum service remove D:/apis/payments --path C:/servicios --keep-files --yes   # solo lo saca del sistema
```

Se elimina el servicio y lo que cuelga de él:

- su carpeta (código, tests, migraciones, `msg.json`). Con `--keep-files` (solo para servicios de otras carpetas) se conserva;
- las suscripciones de otros servicios a sus eventos: sus contratos y los handlers que no hayas tocado (de los que sí, avisa);
- su entrada en `msg.workspace.json` y sus referencias en el AppHost y la solución, que se regeneran.

La base de datos y los topics de Kafka viven en los contenedores de Aspire: Stratum te dice cuáles son para que los borres
si ya no los necesitas. En el Studio: **General → Zona peligrosa → Eliminar**, con el mismo resumen y confirmación escribiendo
el nombre del servicio.

Al quitar una entidad o dejar de publicar sus eventos, los servicios que los escuchaban aparecen marcados en el mapa de
eventos; `stratum subscribe --sync --prune` (o *Quitar* en su Mensajería) limpia esas suscripciones.

## Servicios en otras carpetas (`msg.workspace.json`)

Un sistema es una carpeta con servicios dentro, pero un servicio puede vivir en otra ruta (otro disco, otro repositorio):

```bash
stratum new Payments -o D:/apis/payments
stratum workspace add D:/apis/payments --path C:/servicios   # lo apunta en C:/servicios/msg.workspace.json
stratum workspace list --path C:/servicios
stratum aspire --path C:/servicios                            # el AppHost lo incluye
```

En el Studio, **Nuevo servicio → Ubicación** (con un explorador de carpetas) y **Añadir un servicio de otra carpeta** hacen lo mismo.

## Mensajería entre servicios (Kafka)

```bash
stratum messaging on --broker kafka        # o inMemory (solo dentro del proceso)
stratum events on Product                  # publica ProductCreated / ProductUpdated / ProductDeleted
stratum aspire                             # añade Kafka y Kafka UI al AppHost
```

En el Studio: **Servicio → Mensajería entre servicios** (broker) y **Entidades → Eventos para otros servicios** (qué entidad publica).

Qué genera:

- `MsgWiring.g.cs`: `AddMsgMessaging("shop").UseKafka(...).UseEntityFramework<ShopDbContext>()`.
- Tablas `msg_outbox` y `msg_inbox` (`MessagingConfiguration.g.cs`): crea la migración después de activar la mensajería.
- `IntegrationEvents/ProductIntegrationEvents.g.cs`: el contrato público, con topic `shop.product-created`, etc.
  - Vertical slice: los endpoints publican con `IEventBus` en la misma transacción que el cambio.
  - Clean + DDD: `ProductIntegrationEventsMapper.g.cs` convierte los eventos de dominio en eventos de integración al guardar.
- Tests: el broker se sustituye por uno en memoria (`ApiFactory.g.cs`), no hace falta Kafka.
- AppHost: `AddKafka("kafka").WithKafkaUI()` y `WithReference(kafka).WaitFor(kafka)` en cada servicio con Kafka.

### Escuchar eventos de otro servicio

```bash
stratum subscribe ../orders Order.Created --path billing   # o "Order" para Created, Updated y Deleted
stratum unsubscribe Orders Order.Created --path billing
stratum subscribe --sync --path billing                    # si Orders cambió los datos del evento
stratum events map                                         # quién publica y quién escucha cada evento
```

- Stratum copia el contrato en `IntegrationEvents/Orders/OrderCreated.g.cs` (en `msg.json` queda una copia de sus datos, así que
  el servicio se genera aunque `orders` esté en otro repositorio) y crea `Events/Orders/OrderCreatedHandler.cs`, que es tuyo.
- El handler se ejecuta una vez por evento (inbox), con reintentos y dead-letter; lo que guardes en él se confirma junto con
  el registro del inbox.
- En el Studio: **Servicio → Eventos que escucha** (elegir de los eventos que publican los demás servicios, actualizar el
  contrato cuando cambia) y **Sistema → Mapa de eventos**, que avisa de contratos desactualizados.

Paquetes:

| Paquete | Qué hace |
| --- | --- |
| `Msg.Messaging` | `IEventBus` (outbox), handlers `IIntegrationEventHandler<T>` que se ejecutan una vez por evento (inbox), reintentos con espera creciente y dead-letter, trazas de extremo a extremo y transporte en memoria. Sin dependencias de terceros. |
| `Msg.Messaging.EntityFrameworkCore` | Outbox e inbox en la base de datos del servicio: el evento se guarda en la misma transacción que tus cambios, y el handler y su registro en el inbox también. |
| `Msg.Messaging.Kafka` | Kafka con Confluent.Kafka: un topic por evento, un consumer group por servicio, commit tras procesar, topic `<topic>.dead-letter`, topics creados al primer uso. |

## Qué genera

```
Orders/
├── msg.json                     ← definición del servicio (la fuente de verdad)
├── Orders.slnx
├── Directory.Build.props        ← net10.0, nullable, warnings = errores, analyzers
├── Directory.Packages.props     ← Central Package Management
├── global.json  .editorconfig  .gitignore
├── Dockerfile  .dockerignore    ← multi-stage, usuario no-root
├── .github/workflows/ci.yml
├── src/Orders.Api/              ← lo propio del servicio: endpoints, opciones
└── tests/Orders.Api.Tests/      ← tests del contrato común
```

## Paquetes `Msg.ServiceDefaults.*`

Uno por feature, para que quien no use una no arrastre sus dependencias:

| Paquete | Qué aporta | Dependencias |
| --- | --- | --- |
| `Msg.Ddd` | `AggregateRoot`, `DomainEvent`, `DomainException`, `ICommand`/`IQuery` y sus handlers | Ninguna |
| `Msg.ServiceDefaults` | `AddMsgServiceDefaults()`, `UseMsgServiceDefaults()`, `MapMsgDefaultEndpoints()`, `AddValidatedOptions<T>()`, `AddMsgApplication()` | Solo ASP.NET Core + `Msg.Ddd` |
| `Msg.ServiceDefaults.OpenTelemetry` | `AddMsgOpenTelemetry("servicio")` | OpenTelemetry |
| `Msg.ServiceDefaults.Resilience` | `AddMsgResilience()`, `AddMsgServiceDiscovery()` | Microsoft.Extensions.Http.Resilience, Microsoft.Extensions.ServiceDiscovery |
| `Msg.ServiceDefaults.Authentication` | `AddMsgAuthentication()` (sección `Auth`) | JwtBearer |

Todos comparten versión con Stratum (`<Version>` en `Directory.Build.props`). Las versiones de sus dependencias viven en `Directory.Packages.props` de este repo; un servicio generado solo fija `Msg.*`.

**Flujo de una actualización de seguridad:** subir la dependencia aquí → nueva versión de Stratum → en cada servicio, `stratum upgrade` → PR.

| Siempre incluido | Opcional (`--no-x` / `--x`) |
| --- | --- |
| Health checks `/health/live` y `/health/ready` | `observability`: OpenTelemetry (trazas, métricas, logs) |
| Errores `application/problem+json` con `traceId` y `correlationId` | `openApi`: documento OpenAPI + Scalar en `/scalar` (solo Development) |
| `X-Correlation-ID` en respuesta y en cada log | `resilience`: retry/timeout/circuit breaker en todo `HttpClient` |
| Logs JSON en producción y legibles en desarrollo | `auth`: JWT bearer, autenticado por defecto (**off** por defecto) |
| Configuración validada al arrancar (`AddValidatedOptions<T>`) | `tests`, `docker`, `ci` |
| API versionada (`/api/v1`) y puerto local estable por servicio | |

## Cómo funciona

```
msg.json ──► ServiceDefinition ──► ServiceGenerator (puro, en memoria) ──► FileWriter (CLI)
                                         │                                └─► zip / repo (editor visual, futuro)
                                         └── templates/service/** (recursos incrustados)
```

- **`src/Msg.Core`**: modelo, motor de plantillas y generador. No escribe en disco, así que el editor visual podrá reutilizarlo tal cual.
- **`src/Msg.Cli`**: la CLI (`new`, `add entity`, `aspire`, `studio`, `upgrade`). Sin dependencias externas: solo .NET y ASP.NET Core.
- **`src/Msg.Studio`**: el editor visual (componentes Blazor y estado), sobre `Msg.Core`.
- **`src/Msg.ServiceDefaults*`**: los paquetes que consumen los servicios generados, con sus tests en `tests/`.
- **`templates/service`**: plantillas como archivos reales (C#, XML, YAML, JSON), fáciles de leer y editar:
  - `{{Service}}`, `{{service-kebab}}`, `{{DevPort}}` y `{{MsgVersion}}` en el contenido; `__Service__` en las rutas.
  - Líneas con `@msg:if feature`, `@msg:if !feature`, `@msg:else` y `@msg:end` dentro de cualquier comentario. La línea de la directiva se elimina del resultado.
  - Un archivo cuya plantilla queda vacía no se genera.
  - `_gitignore` → `.gitignore` y `_github/` → `.github/`, para que no haya archivos ocultos en las plantillas.

## Desarrollo

```bash
dotnet build Msg.slnx
dotnet test Msg.slnx

# Mientras los paquetes no estén en nuget.org: empaqueta en local y apunta el servicio a esa carpeta.
dotnet pack Msg.slnx -c Release -o ./artifacts/packages
dotnet run --project src/Msg.Cli -- new Orders -o ../Orders --package-source ./artifacts/packages
```

`--package-source` genera en el servicio un `nuget.config` con *package source mapping*: `Msg.*` sale solo de esa carpeta y todo lo demás solo de nuget.org. Si reempaquetas la misma versión, borra la caché: `dotnet nuget locals global-packages --clear` (el smoke test ya lo hace solo para `Msg.*`).

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
dotnet pack src/Msg.Cli -c Release -o ./artifacts/packages
dotnet tool install -g Msg.Cli --add-source ./artifacts/packages
```

## Roadmap

1. ✅ **Fase 1**: CLI + servicio con la base común.
2. **Fase 2**
   - ✅ 2a: lo común en paquetes `Msg.ServiceDefaults.*` + `stratum upgrade`.
   - ✅ 2b: `stratum add entity` con CRUD, EF Core (PostgreSQL o SQL Server) y tests con SQLite en memoria.
   - 2c: publicar los paquetes en nuget.org desde CI al crear un tag.
3. ✅ `stratum aspire`: AppHost de Aspire para todos los servicios.
4. ✅ **Fase 3**: editor visual `stratum studio` (Blazor Server sobre `Msg.Core`).
5. ✅ Entidades editables: cambiar, renombrar y eliminar propiedades y entidades sin pisar tu código (`stratum entity`, Studio).
6. ✅ Migraciones guiadas: renombrados sin perder datos, avisos de lo que borra datos, SQL y deshacer (`stratum migration review`, Studio).
7. ✅ Features editables en servicios existentes (`stratum feature`, Studio), paquetes y archivos que sobran se quitan, `.g.cs` editados a mano detectados.
8. En curso: mensajería entre servicios. ✅ `Msg.Messaging`, outbox/inbox con EF Core, Kafka con Aspire, publicar eventos de entidades (CLI y Studio). ✅ Suscripciones y mapa de eventos. Siguiente: Azure Service Bus; `Idempotency-Key` en la API.
9. Después: métodos propios del agregado y relaciones entre entidades desde el Studio; publicar en nuget.org.
4. **Fase 4**: varios servicios conectados (eventos, Aspire AppHost, docker-compose).
