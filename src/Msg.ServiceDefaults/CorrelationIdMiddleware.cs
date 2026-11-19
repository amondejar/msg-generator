using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Msg.ServiceDefaults;

/// <summary>
/// Reads <c>X-Correlation-ID</c> (or creates one from the trace id), returns it in the response,
/// and adds it to the logging scope so every log line of the request carries it.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "CorrelationId";
    private const int MaxLength = 128;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId =
            context.Request.Headers.TryGetValue(HeaderName, out var header) && IsValid(header)
                ? header.ToString()
                : Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        context.Items[ItemKey] = correlationId;
        Activity.Current?.SetTag("correlation.id", correlationId);

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { [ItemKey] = correlationId }))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    // Only accept safe, bounded values: the header comes from the outside and ends up in logs.
    private static bool IsValid(StringValues value) =>
        value.Count == 1
        && value[0] is { Length: > 0 and <= MaxLength } id
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
