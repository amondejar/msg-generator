using Microsoft.EntityFrameworkCore;

namespace {{Service}}.Api.Data;

/// <summary>
/// DbSets are generated in <c>{{Service}}DbContext.g.cs</c>. Each entity is configured in its own
/// <c>IEntityTypeConfiguration</c> next to it (Features/...), picked up automatically below.
/// </summary>
public sealed partial class {{Service}}DbContext(DbContextOptions<{{Service}}DbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof({{Service}}DbContext).Assembly);
    }
}
