using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Msg.ServiceDefaults;

/// <summary>
/// What every Stratum service does the same way. A generated Program.cs calls, in this order:
/// <c>builder.AddMsgServiceDefaults()</c>, <c>app.UseMsgServiceDefaults()</c>, <c>app.MapMsgDefaultEndpoints()</c>.
/// </summary>
public static class MsgServiceDefaultsExtensions
{
    public const string HealthPath = "/health";
    public const string LivenessPath = HealthPath + "/live";
    public const string ReadinessPath = HealthPath + "/ready";

    /// <summary>
    /// Registers health checks and ProblemDetails. Add dependency checks (database, broker...)
    /// with <c>builder.Services.AddHealthChecks().AddXxx()</c>: without the <see cref="HealthTags.Live"/> tag
    /// they only affect readiness, which is what you want.
    /// </summary>
    public static TBuilder AddMsgServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddMsgProblemDetails();
        builder.Services.AddExceptionHandler<DomainExceptionHandler>();
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), [HealthTags.Live]);

        return builder;
    }

    /// <summary>
    /// Correlation id → exception handler (unhandled exceptions as ProblemDetails) → status code pages
    /// (empty 4xx/5xx as ProblemDetails). Call it first, before authentication and endpoints.
    /// </summary>
    public static WebApplication UseMsgServiceDefaults(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        return app;
    }

    /// <summary>
    /// Maps <c>/health/live</c> (only checks tagged <see cref="HealthTags.Live"/>) and <c>/health/ready</c> (all checks).
    /// Both allow anonymous access so Kubernetes probes work when authentication is enabled.
    /// </summary>
    public static WebApplication MapMsgDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var health = app.MapGroup(HealthPath);
        health.MapHealthChecks("/live", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(HealthTags.Live),
        });
        health.MapHealthChecks("/ready");
        health.AllowAnonymous();

        return app;
    }
}

public static class HealthTags
{
    /// <summary>Checks with this tag are part of liveness. Use it only for "the process itself is broken" checks.</summary>
    public const string Live = "live";
}
