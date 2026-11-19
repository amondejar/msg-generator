using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace {{Service}}.ServiceDefaults;

/// <summary>
/// Entry points used by Program.cs. Everything a service must do "the same way" is wired here,
/// so Program.cs only contains what is specific to this service.
/// </summary>
public static class ServiceDefaultsExtensions
{
    private const string LiveTag = "live";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddDefaultErrorHandling();

        builder.Services.AddHealthChecks()
            // Liveness: the process is up. Add dependency checks (DB, broker...) without the "live" tag
            // so they only affect readiness.
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);

        // @msg:if observability
        builder.AddDefaultOpenTelemetry();
        // @msg:end
        // @msg:if resilience

        // Every HttpClient gets retry + timeout + circuit breaker by default.
        builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
        // @msg:end
        // @msg:if auth

        builder.AddDefaultAuthentication();
        // @msg:end

        return builder;
    }

    /// <summary>Middleware every service needs, in the right order.</summary>
    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();   // unhandled exceptions → ProblemDetails (RFC 9457)
        app.UseStatusCodePages();    // empty 4xx/5xx (404, 401...) → ProblemDetails
        // @msg:if auth
        app.UseAuthentication();
        app.UseAuthorization();
        // @msg:end

        return app;
    }

    /// <summary>Maps <c>/health/live</c> (liveness) and <c>/health/ready</c> (readiness) for Kubernetes.</summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var health = app.MapGroup("/health");
        health.MapHealthChecks("/live", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(LiveTag),
        });
        health.MapHealthChecks("/ready");
        // @msg:if auth
        health.AllowAnonymous();
        // @msg:end

        return app;
    }
}
