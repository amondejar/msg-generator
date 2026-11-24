# {{Service}}

Servicio generado con [Stratum](https://github.com/) {{StratumVersion}}. La definición está en `stratum.json`.

## Arrancar en local

```bash
dotnet run --project src/{{Service}}.Api
```

- API: http://localhost:{{DevPort}}/api/v1/ping
- Liveness: http://localhost:{{DevPort}}/health/live
- Readiness: http://localhost:{{DevPort}}/health/ready
<!-- @stratum:if openApi -->
- Scalar, para explorar y probar la API (solo Development): http://localhost:{{DevPort}}/scalar
- OpenAPI (solo Development): http://localhost:{{DevPort}}/openapi/v1.json
<!-- @stratum:end -->

## Estructura

<!-- @stratum:if cleanDdd -->
Clean Architecture + DDD. Las dependencias apuntan hacia el dominio: `Api → Application → Domain` e `Infrastructure → Application`.

| Carpeta | Qué contiene |
| --- | --- |
| `src/{{Service}}.Domain` | Agregados, eventos de dominio y contratos de repositorio. Sin frameworks: solo `Stratum.Ddd`. |
| `src/{{Service}}.Application` | Casos de uso: commands, queries y handlers de eventos de dominio. |
| `src/{{Service}}.Infrastructure` | EF Core, repositorios y el interceptor que publica los eventos de dominio tras guardar. |
| `src/{{Service}}.Api` | Endpoints finos que llaman a los casos de uso. Sin lógica de negocio. |
<!-- @stratum:else -->
| Carpeta | Qué contiene |
| --- | --- |
| `src/{{Service}}.Api` | Endpoints y configuración propia de este servicio. **Aquí va tu código.** |
<!-- @stratum:end -->
<!-- @stratum:if tests -->
| `tests/{{Service}}.Api.Tests` | Tests de integración del contrato común. |
<!-- @stratum:end -->

<!-- @stratum:if persistence -->
## Base de datos

<!-- @stratum:if postgres -->
PostgreSQL con EF Core. Para levantar una base de datos local:
<!-- @stratum:else -->
SQL Server con EF Core. Para levantar una base de datos local:
<!-- @stratum:end -->

```bash
dotnet tool install -g dotnet-ef                                # una sola vez
docker compose up -d
<!-- @stratum:if cleanDdd -->
dotnet ef migrations add Initial --project src/{{Service}}.Infrastructure --startup-project src/{{Service}}.Api
dotnet ef database update --project src/{{Service}}.Infrastructure --startup-project src/{{Service}}.Api
<!-- @stratum:else -->
dotnet ef migrations add Initial --project src/{{Service}}.Api   # la primera vez y tras cada cambio de modelo
dotnet ef database update --project src/{{Service}}.Api
<!-- @stratum:end -->
```

En producción, la cadena de conexión se pasa por la variable `ConnectionStrings__Database`. Si falta, el servicio no arranca.
Los tests no necesitan Docker, porque usan SQLite en memoria (`tests/{{Service}}.Api.Tests/ApiFactory.Database.g.cs`).

<!-- @stratum:end -->
## Entidades

```bash
msg add entity Product --props "Name:string, Price:decimal, Description:string?"
```

Genera en `src/{{Service}}.Api/Features/<Plural>/` la entidad, su mapeo de EF, los DTOs con validación, los endpoints CRUD paginados
(`/api/v1/<plural>`) y sus tests. Esos archivos son tuyos: Stratum no los vuelve a tocar. Los `*.g.cs` los regenera Stratum en cada `stratum add`.

## Lo común viene en paquetes

Lo que todos los servicios hacen igual no está copiado aquí: viene de los paquetes `Stratum.ServiceDefaults.*`
(versión en `Directory.Packages.props`). Para recibir mejoras y parches de seguridad:

```bash
msg upgrade
```

## Convenciones incluidas

- **Errores**: todas las respuestas de error son `application/problem+json` (RFC 9457) con `traceId` y `correlationId`.
- **Correlation ID**: se lee de `X-Correlation-ID` (o se genera) y se devuelve en la respuesta y en cada log.
- **Logs**: JSON estructurado en producción y una línea legible en Development.
- **Configuración**: secciones validadas al arrancar con `AddValidatedOptions<T>()`. Si falta algo, el servicio no arranca.
- **Health checks**: `/health/live` (el proceso vive) y `/health/ready` (dependencias listas).
<!-- @stratum:if observability -->
- **Telemetría**: OpenTelemetry (trazas, métricas y logs). Define `OTEL_EXPORTER_OTLP_ENDPOINT` para exportar.
<!-- @stratum:end -->
<!-- @stratum:if resilience -->
- **Resiliencia**: todos los `HttpClient` tienen reintentos, timeout y circuit breaker.
<!-- @stratum:end -->
<!-- @stratum:if auth -->
- **Seguridad**: JWT bearer (sección `Auth`). Todo requiere autenticación salvo lo marcado como `AllowAnonymous`.
<!-- @stratum:end -->
- **Paquetes**: versiones centralizadas en `Directory.Packages.props`.
<!-- @stratum:if docker -->

## Docker

```bash
docker build -t {{service-kebab}} .
docker run -p 8080:8080 {{service-kebab}}
```
<!-- @stratum:end -->
