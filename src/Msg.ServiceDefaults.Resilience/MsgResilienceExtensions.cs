using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Msg.ServiceDefaults;

public static class MsgResilienceExtensions
{
    /// <summary>
    /// Every <see cref="HttpClient"/> created through IHttpClientFactory gets the standard resilience
    /// pipeline: rate limiter, total timeout, retry with backoff, circuit breaker and attempt timeout.
    /// Tune a specific client with <c>AddHttpClient&lt;T&gt;().AddStandardResilienceHandler(o =&gt; ...)</c>.
    /// </summary>
    public static TBuilder AddMsgResilience<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
        return builder;
    }
}
