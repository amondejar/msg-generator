# MSG: generador de microservicios .NET

MSG genera microservicios .NET 10 que nacen con lo que cualquier equipo serio exige siempre igual: estructura, observabilidad, errores, configuración validada, tests, Docker y CI. Lo común no se copia en cada servicio: llega en los paquetes `Msg.ServiceDefaults.*`, así que mejorarlo o parchearlo es subir una versión. El objetivo final es un editor visual.

```bash
msg new Orders                 # servicio completo
msg new Billing --auth         # + JWT, autenticado por defecto
msg new Catalog --no-docker    # quita lo que no necesites
msg new --from msg.json        # regenera desde una definición
msg new Orders --dry-run       # lista los archivos sin escribir nada
msg upgrade                    # actualiza los paquetes Msg.* de un servicio a la versión de MSG instalada

# Dentro de un servicio:
msg add entity Product --props "Name:string, Price:decimal, Description:string?"
msg add entity Invoice --props "Number:string, Total:decimal" --db sqlserver   # la primera entidad elige la BD
```

## `msg add entity`

Por cada entidad genera la entidad (Id UUID v7, `CreatedAt`, `UpdatedAt`), su `IEntityTypeConfiguration`, los DTOs con validación
(400 ProblemDetails gracias a la validación integrada de .NET 10), los endpoints CRUD paginados y sus tests. La primera entidad añade
además el `DbContext`, el health check de base de datos (readiness), `compose.yaml` y una base SQLite en memoria para los tests.

Tipos de `--props`: `string`, `int`, `long`, `decimal`, `bool`, `guid`, `date`, `datetime`. Con `?` la propiedad es opcional.

Reglas para no pisar tu código:

| Archivos | Qué hace MSG |
| --- | --- |
| `*.g.cs` (registro de endpoints, `DbSet`s, base de test) | Los regenera siempre a partir de `msg.json`. |
| Entidad, endpoints, DTOs, tests, `DbContext`, `compose.yaml` | Los crea una vez. A partir de ahí son tuyos. |
| `.csproj`, `Directory.Packages.props`, `appsettings.Development.json` | Solo añade lo que falta. |
| Todo lo demás | No lo toca. |

La entidad queda en `msg.json`, así que `msg new --from msg.json` reconstruye el servicio con todas sus entidades.

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
| `Msg.ServiceDefaults` | `AddMsgServiceDefaults()`, `UseMsgServiceDefaults()`, `MapMsgDefaultEndpoints()`, `AddValidatedOptions<T>()` | Solo ASP.NET Core |
| `Msg.ServiceDefaults.OpenTelemetry` | `AddMsgOpenTelemetry("servicio")` | OpenTelemetry |
| `Msg.ServiceDefaults.Resilience` | `AddMsgResilience()` | Microsoft.Extensions.Http.Resilience |
| `Msg.ServiceDefaults.Authentication` | `AddMsgAuthentication()` (sección `Auth`) | JwtBearer |

Todos comparten versión con MSG (`<Version>` en `Directory.Build.props`). Las versiones de sus dependencias viven en `Directory.Packages.props` de este repo; un servicio generado solo fija `Msg.*`.

**Flujo de una actualización de seguridad:** subir la dependencia aquí → nueva versión de MSG → en cada servicio, `msg upgrade` → PR.

| Siempre incluido | Opcional (`--no-x` / `--x`) |
| --- | --- |
| Health checks `/health/live` y `/health/ready` | `observability`: OpenTelemetry (trazas, métricas, logs) |
| Errores `application/problem+json` con `traceId` y `correlationId` | `openApi`: documento OpenAPI |
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
- **`src/Msg.Cli`**: CLI sin dependencias externas (`new`, `upgrade`).
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
   - ✅ 2a: lo común en paquetes `Msg.ServiceDefaults.*` + `msg upgrade`.
   - ✅ 2b: `msg add entity` con CRUD, EF Core (PostgreSQL o SQL Server) y tests con SQLite en memoria.
   - 2c: publicar los paquetes en nuget.org desde CI al crear un tag.
3. **Fase 3**: editor visual sobre `msg.json`.
4. **Fase 4**: varios servicios conectados (eventos, Aspire AppHost, docker-compose).
