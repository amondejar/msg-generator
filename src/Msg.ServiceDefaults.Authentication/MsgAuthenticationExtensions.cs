using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Msg.ServiceDefaults;

/// <summary>"Auth" configuration section.</summary>
public sealed class MsgAuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>OIDC issuer, e.g. https://login.microsoftonline.com/{tenant}/v2.0 or your Keycloak realm.</summary>
    [Required, Url]
    public string Authority { get; init; } = string.Empty;

    [Required]
    public string Audience { get; init; } = string.Empty;
}

public static class MsgAuthenticationExtensions
{
    /// <summary>
    /// JWT bearer auth configured from the "Auth" section (validated at startup). Secure by default:
    /// every endpoint requires an authenticated user unless marked <c>AllowAnonymous()</c>
    /// (health checks already are). Remember <c>app.UseAuthentication(); app.UseAuthorization();</c>.
    /// </summary>
    public static TBuilder AddMsgAuthentication<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddValidatedOptions<MsgAuthOptions>(MsgAuthOptions.SectionName);

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<MsgAuthOptions>>((jwt, auth) =>
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
