using {{Service}}.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace {{Service}}.Infrastructure.Persistence;

public static class PersistenceExtensions
{
    public const string ConnectionStringName = "Database";

    /// <summary>
    /// DbContext (also the <see cref="IUnitOfWork"/>), repositories, the interceptor that publishes
    /// domain events after saving, and a "database" readiness health check.
    /// </summary>
    public static WebApplicationBuilder AddPersistence(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var connectionString = builder.Configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Fail at startup, not at the first query. In production set ConnectionStrings__Database.
            throw new InvalidOperationException(
                $"Missing connection string 'ConnectionStrings:{ConnectionStringName}'.");
        }

        builder.Services.AddScoped<IInterceptor, DomainEventsInterceptor>();
        builder.Services.AddDbContext<{{Service}}DbContext>((services, options) => options
            // @msg:if postgres
            .UseNpgsql(connectionString)
            // @msg:else
            .UseSqlServer(connectionString)
            // @msg:end
            .AddInterceptors(services.GetServices<IInterceptor>()));

        builder.Services.AddScoped<IUnitOfWork>(services => services.GetRequiredService<{{Service}}DbContext>());
        builder.Services.AddRepositories();
        builder.Services.AddHealthChecks().AddDbContextCheck<{{Service}}DbContext>("database");

        if (builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            builder.Services.AddHostedService<DatabaseMigrator>();
        }

        return builder;
    }
}
