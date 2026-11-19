// @msg:if observability
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace {{Service}}.ServiceDefaults;

internal static class Observability
{
    public const string ServiceName = "{{service-kebab}}";

    /// <summary>
    /// Traces, metrics and logs with OpenTelemetry. Export is enabled by setting
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (Aspire dashboard, Jaeger, Grafana, Datadog agent...).
    /// </summary>
    public static TBuilder AddDefaultOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(ServiceName)
                .AddAspNetCoreInstrumentation(options =>
                    // Don't fill the traces with Kubernetes probes.
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }
}
// @msg:end
