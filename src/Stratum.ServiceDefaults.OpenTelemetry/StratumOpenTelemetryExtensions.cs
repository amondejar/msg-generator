using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Stratum.ServiceDefaults;

public static class StratumOpenTelemetryExtensions
{
    /// <summary>The OTLP endpoint variable from the OpenTelemetry spec. When it is not set, nothing is exported.</summary>
    public const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>
    /// Traces, metrics and logs with OpenTelemetry. Export is enabled by setting
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (Aspire dashboard, Jaeger, Grafana, Datadog agent...).
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="serviceName">
    /// OTel <c>service.name</c>. Also registered as an ActivitySource name, so
    /// <c>new ActivitySource(serviceName)</c> spans are exported. Defaults to the application name.
    /// </param>
    public static TBuilder AddStratumOpenTelemetry<TBuilder>(this TBuilder builder, string? serviceName = null)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        serviceName ??= builder.Environment.ApplicationName;

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(serviceName)
                .AddSource("Stratum.Messaging")   // integration events: publish and process spans, one trace across services
                .AddAspNetCoreInstrumentation(options =>
                    // Don't fill the traces with Kubernetes probes.
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments(StratumServiceDefaultsPaths.Health))
                .AddHttpClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpointVariable]))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }
}

// Kept local so this package doesn't depend on Stratum.ServiceDefaults (core) for a single constant.
internal static class StratumServiceDefaultsPaths
{
    public const string Health = "/health";
}
