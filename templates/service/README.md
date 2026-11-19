# {{Service}}

Servicio generado con [MSG](https://github.com/) {{MsgVersion}}. La definición está en `msg.json`.

## Arrancar en local

```bash
dotnet run --project src/{{Service}}.Api
```

- API: http://localhost:{{DevPort}}/api/v1/ping
- Liveness: http://localhost:{{DevPort}}/health/live
- Readiness: http://localhost:{{DevPort}}/health/ready
<!-- @msg:if openApi -->
- OpenAPI (solo Development): http://localhost:{{DevPort}}/openapi/v1.json
<!-- @msg:end -->

## Estructura

| Carpeta | Qué contiene |
| --- | --- |
| `src/{{Service}}.Api` | Endpoints y configuración propia de este servicio. **Aquí va tu código.** |
<!-- @msg:if tests -->
| `tests/{{Service}}.Api.Tests` | Tests de integración del contrato común. |
<!-- @msg:end -->

## Lo común viene en paquetes

Lo que todos los servicios hacen igual no está copiado aquí: viene de los paquetes `Msg.ServiceDefaults.*`
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
<!-- @msg:if observability -->
- **Telemetría**: OpenTelemetry (trazas, métricas y logs). Define `OTEL_EXPORTER_OTLP_ENDPOINT` para exportar.
<!-- @msg:end -->
<!-- @msg:if resilience -->
- **Resiliencia**: todos los `HttpClient` tienen reintentos, timeout y circuit breaker.
<!-- @msg:end -->
<!-- @msg:if auth -->
- **Seguridad**: JWT bearer (sección `Auth`). Todo requiere autenticación salvo lo marcado como `AllowAnonymous`.
<!-- @msg:end -->
- **Paquetes**: versiones centralizadas en `Directory.Packages.props`.
<!-- @msg:if docker -->

## Docker

```bash
docker build -t {{service-kebab}} .
docker run -p 8080:8080 {{service-kebab}}
```
<!-- @msg:end -->
