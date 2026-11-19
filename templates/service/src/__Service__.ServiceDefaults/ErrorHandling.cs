using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace {{Service}}.ServiceDefaults;

internal static class ErrorHandling
{
    /// <summary>
    /// Every error leaves the service with the same shape: <c>application/problem+json</c>
    /// including <c>traceId</c> and <c>correlationId</c>, so a client can always quote them in a bug report.
    /// </summary>
    public static IServiceCollection AddDefaultErrorHandling(this IServiceCollection services) =>
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var http = context.HttpContext;
            context.ProblemDetails.Instance ??= $"{http.Request.Method} {http.Request.Path}";
            context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.Id ?? http.TraceIdentifier);

            if (http.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var correlationId))
            {
                context.ProblemDetails.Extensions.TryAdd("correlationId", correlationId);
            }
        });
}
