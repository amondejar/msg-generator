using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
// @msg:if auth
using Microsoft.AspNetCore.Authentication;
// @msg:end

namespace {{Service}}.Api.Tests;

/// <summary>
/// The service running in memory, with test replacements for its external dependencies.
/// Generated parts (e.g. the SQLite database) live in ApiFactory.*.g.cs.
/// </summary>
public sealed partial class ApiFactory : WebApplicationFactory<Program>
{
    // @msg:if auth
    /// <summary>A client that is authenticated as a test user (see <see cref="TestAuthHandler"/>).</summary>
    public HttpClient CreateAuthenticatedClient(string user = "test-user")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, user);
        return client;
    }

    // @msg:end
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            // @msg:if auth
            // Real JWT validation needs an identity provider; tests use a header-based scheme instead.
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });

            // @msg:end
            ConfigureTestServices(services);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        OnHostCreated(host);
        return host;
    }

    partial void ConfigureTestServices(IServiceCollection services);

    partial void OnHostCreated(IHost host);
}
