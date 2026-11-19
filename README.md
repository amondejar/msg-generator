# MSG: generador de microservicios .NET

MSG genera microservicios .NET 10 que nacen con lo que cualquier equipo serio exige siempre igual: estructura, observabilidad, errores, configuración validada, tests, Docker y CI. El objetivo final es un editor visual. Esta es la **fase 1**: el motor y la CLI.

```bash
msg new Orders                 # servicio completo
msg new Billing --auth         # + JWT, autenticado por defecto
msg new Catalog --no-docker    # quita lo que no necesites
msg new --from msg.json        # regenera desde una definición
msg new Orders --dry-run       # lista los archivos sin escribir nada
```

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
├── src/
│   ├── Orders.Api/              ← lo propio del servicio: endpoints, opciones
│   └── Orders.ServiceDefaults/  ← lo que todos hacen igual
└── tests/Orders.Api.Tests/      ← tests del contrato común
```

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
- **`src/Msg.Cli`**: CLI sin dependencias externas.
- **`templates/service`**: plantillas como archivos reales (C#, XML, YAML, JSON), fáciles de leer y editar:
  - `{{Service}}`, `{{service-kebab}}`, `{{DevPort}}` y `{{MsgVersion}}` en el contenido; `__Service__` en las rutas.
  - Líneas con `@msg:if feature`, `@msg:if !feature`, `@msg:else` y `@msg:end` dentro de cualquier comentario. La línea de la directiva se elimina del resultado.
  - Un archivo cuya plantilla queda vacía no se genera.
  - `_gitignore` → `.gitignore` y `_github/` → `.github/`, para que no haya archivos ocultos en las plantillas.

## Desarrollo

```bash
dotnet build Msg.slnx
dotnet run --project src/Msg.Cli -- new Orders -o ../Orders
```

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
dotnet pack src/Msg.Cli -c Release
dotnet tool install -g Msg.Cli --add-source ./artifacts
```

## Roadmap

1. ✅ **Fase 1**: CLI + servicio con la base común.
2. **Fase 2**: sacar `ServiceDefaults` a paquetes NuGet versionados (`Msg.ServiceDefaults`), para que mejorar la base sea subir una versión en todos los servicios. Añadir `msg add entity` con CRUD y persistencia (EF Core).
3. **Fase 3**: editor visual sobre `msg.json`.
4. **Fase 4**: varios servicios conectados (eventos, Aspire AppHost, docker-compose).
