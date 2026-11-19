using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace {{Service}}.ServiceDefaults;

public static class OptionsExtensions
{
    /// <summary>
    /// Binds a config section to <typeparamref name="TOptions"/>, validates DataAnnotations and
    /// fails at startup (not at the first request) if configuration is missing or wrong.
    /// </summary>
    public static OptionsBuilder<TOptions> AddValidatedOptions<TOptions>(
        this IServiceCollection services, string sectionName)
        where TOptions : class =>
        services.AddOptions<TOptions>()
            .BindConfiguration(sectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
}
