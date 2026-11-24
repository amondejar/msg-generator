using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stratum.ServiceDefaults;

/// <summary>Configuration section "Idempotency".</summary>
public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    public const string HeaderName = "Idempotency-Key";

    /// <summary>Response header on a replayed response.</summary>
    public const string ReplayedHeaderName = "Idempotent-Replayed";

    /// <summary>How long a key remembers its response. Retries after that run again.</summary>
    public TimeSpan Expiration { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Requests or responses bigger than this are not remembered (they run normally).</summary>
    public int MaxBodyBytes { get; set; } = 1024 * 1024;
}

/// <summary>
/// "Idempotency-Key" header on POST, PUT, PATCH and DELETE: the first request runs and its response is kept; a retry
/// with the same key (a timeout, a double click) gets the same response without running again
/// ("Idempotent-Replayed: true"). The same key with a different request is a 422, and a retry while the first one is
/// still running is a 409. Without the header nothing changes. Responses are kept in IDistributedCache: in memory by
/// default (one instance); register Redis (AddStackExchangeRedisCache) to share them between instances.
/// </summary>
internal sealed partial class IdempotencyMiddleware(
    RequestDelegate next,
    IDistributedCache cache,
    IOptions<IdempotencyOptions> options,
    ILogger<IdempotencyMiddleware> logger)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsPost(request.Method) && !HttpMethods.IsPut(request.Method)
            && !HttpMethods.IsPatch(request.Method) && !HttpMethods.IsDelete(request.Method))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!request.Headers.TryGetValue(IdempotencyOptions.HeaderName, out var values))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var key = values.ToString();
        if (key.Length is 0 or > 255)
        {
            await Problem(context, StatusCodes.Status400BadRequest, "Idempotency-Key no válida", "Debe tener entre 1 y 255 caracteres.").ConfigureAwait(false);
            return;
        }

        var settings = options.Value;
        if (request.ContentLength > settings.MaxBodyBytes)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var fingerprint = await FingerprintAsync(request, settings.MaxBodyBytes, context.RequestAborted).ConfigureAwait(false);

        // Keys are per caller: the same key from another user (another Authorization header) is another request.
        var cacheKey = $"msg:idempotency:{Hash(request.Headers.Authorization.ToString())}:{request.Method}:{request.Path}:{key}";
        var gate = Locks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.Zero, context.RequestAborted).ConfigureAwait(false))
        {
            await Problem(context, StatusCodes.Status409Conflict, "Petición en curso", "Ya se está procesando una petición con esta Idempotency-Key; reinténtala en unos segundos.").ConfigureAwait(false);
            return;
        }

        try
        {
            if (await cache.GetAsync(cacheKey, context.RequestAborted).ConfigureAwait(false) is { } stored)
            {
                var saved = JsonSerializer.Deserialize<SavedResponse>(stored)!;
                if (saved.Fingerprint != fingerprint)
                {
                    await Problem(context, StatusCodes.Status422UnprocessableEntity, "Idempotency-Key reutilizada",
                        "Esta Idempotency-Key ya se usó con otra petición. Usa una clave nueva para cada operación.").ConfigureAwait(false);
                    return;
                }

                LogReplayed(logger, request.Method, request.Path, key);
                context.Response.StatusCode = saved.StatusCode;
                context.Response.ContentType = saved.ContentType;
                if (saved.Location is not null)
                {
                    context.Response.Headers.Location = saved.Location;
                }

                context.Response.Headers[IdempotencyOptions.ReplayedHeaderName] = "true";
                await context.Response.Body.WriteAsync(saved.Body, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            var original = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                context.Response.Body = original;
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false);

            // Only successes are remembered: after an error (a 400 to fix, a 500) the retry runs again.
            if (context.Response.StatusCode < 400 && buffer.Length <= settings.MaxBodyBytes)
            {
                var saved = new SavedResponse(fingerprint, context.Response.StatusCode, context.Response.ContentType,
                    context.Response.Headers.Location.ToString() is { Length: > 0 } location ? location : null, buffer.ToArray());
                await cache.SetAsync(cacheKey, JsonSerializer.SerializeToUtf8Bytes(saved),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = settings.Expiration }, context.RequestAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
            Locks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(cacheKey, gate));
        }
    }

    private static async Task<string> FingerprintAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        request.EnableBuffering(maxBytes);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(request.QueryString.ToString()));
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(chunk, 0, read);
        }

        request.Body.Position = 0;
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static string Hash(string value) =>
        value.Length == 0 ? "anonymous" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private static Task Problem(HttpContext context, int status, string title, string detail) =>
        Results.Problem(title: title, detail: detail, statusCode: status).ExecuteAsync(context);

    private sealed record SavedResponse(string Fingerprint, int StatusCode, string? ContentType, string? Location, byte[] Body);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Method} {Path}: response replayed for Idempotency-Key {Key}")]
    private static partial void LogReplayed(ILogger logger, string method, PathString path, string key);
}
