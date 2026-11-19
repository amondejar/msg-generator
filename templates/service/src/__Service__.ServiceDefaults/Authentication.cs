// @msg:if auth
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace {{Service}}.ServiceDefaults;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>OIDC issuer, e.g. https://login.microsoftonline.com/{tenant}/v2.0 or your Keycloak realm.</summary>
    [Required, Url]
    public string Authority { get; init; } = string.Empty;

    [Required]
    public string Audience { get; init; } = string.Empty;
}

internal static class Authentication
{
    /// <summary>
    /// JWT bearer auth configured from the "Auth" section. Secure by default: every endpoint
    /// requires an authenticated user unless it is explicitly marked AllowAnonymous (health checks are).
    /// </summary>
    public static TBuilder AddDefaultAuthentication<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddValidatedOptions<AuthOptions>(AuthOptions.SectionName);

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
            {
                jwt.Authority = auth.Value.Authority;
                jwt.Audience = auth.Value.Audience;
                jwt.MapInboundClaims = false;
            });

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return builder;
    }
}
// @msg:end
