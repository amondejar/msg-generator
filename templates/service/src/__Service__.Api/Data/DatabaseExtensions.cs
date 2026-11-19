using Microsoft.EntityFrameworkCore;

namespace {{Service}}.Api.Data;

public static class DatabaseExtensions
{
    public const string ConnectionStringName = "Database";

    /// <summary>
    /// Registers the DbContext and a "database" health check. The check has no "live" tag,
    /// so a database outage makes the service not ready (out of the load balancer) instead of restarting it.
    /// </summary>
    public static WebApplicationBuilder AddDatabase(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var connectionString = builder.Configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Fail at startup, not at the first query. In production set ConnectionStrings__Database.
            throw new InvalidOperationException(
                $"Missing connection string 'ConnectionStrings:{ConnectionStringName}'.");
        }

        builder.Services.AddDbContext<{{Service}}DbContext>(options =>
            // @msg:if postgres
            options.UseNpgsql(connectionString));
            // @msg:else
            options.UseSqlServer(connectionString));
            // @msg:end

        builder.Services.AddHealthChecks().AddDbContextCheck<{{Service}}DbContext>("database");

        if (builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            builder.Services.AddHostedService<DatabaseMigrator>();
        }

        return builder;
    }
}
