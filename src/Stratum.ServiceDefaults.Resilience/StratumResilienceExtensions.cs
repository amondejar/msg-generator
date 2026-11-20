using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Stratum.ServiceDefaults;

public static class StratumResilienceExtensions
{
    /// <summary>
    /// Every <see cref="HttpClient"/> created through IHttpClientFactory gets the standard resilience
    /// pipeline: rate limiter, total timeout, retry with backoff, circuit breaker and attempt timeout.
    /// Tune a specific client with <c>AddHttpClient&lt;T&gt;().AddStandardResilienceHandler(o =&gt; ...)</c>.
    /// </summary>
    public static TBuilder AddStratumResilience<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
        return builder;
    }
}

public static class StratumServiceDiscoveryExtensions
{
    /// <summary>
    /// Lets HttpClients call other services by name: <c>http://orders</c> or <c>https+http://orders</c>.
    /// Addresses come from configuration (<c>services:orders:http:0</c>), which the Aspire AppHost sets
    /// automatically for every <c>WithReference(service)</c>.
    /// </summary>
    public static TBuilder AddStratumServiceDiscovery<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());
        return builder;
    }
}
