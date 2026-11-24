using Microsoft.Extensions.Options;

namespace {{Service}}.Api.Endpoints;

/// <summary>
/// Sample endpoint group. One static class per feature/resource keeps Program.cs small.
/// </summary>
public static class PingEndpoints
{
    public static RouteGroupBuilder MapPingEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var ping = group.MapGroup("/ping").WithTags("Ping");

        ping.MapGet(string.Empty,(IOptions<ServiceOptions> options, IHostEnvironment environment) =>
                TypedResults.Ok(new PingResponse(
                    options.Value.Name,
                    options.Value.Version,
                    environment.EnvironmentName)))
            .WithName("Ping")
            .WithSummary("Returns basic information about the running service.");

        return group;
    }
}

public sealed record PingResponse(string Service, string Version, string Environment);
