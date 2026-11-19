using {{Service}}.Application;
using Microsoft.EntityFrameworkCore;

namespace {{Service}}.Infrastructure.Persistence;

/// <summary>
/// Also the <see cref="IUnitOfWork"/>: one SaveChangesAsync commits everything the use case changed.
/// DbSets are generated in <c>{{Service}}DbContext.g.cs</c>; mappings live next to each repository.
/// </summary>
public sealed partial class {{Service}}DbContext(DbContextOptions<{{Service}}DbContext> options) : DbContext(options), IUnitOfWork
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof({{Service}}DbContext).Assembly);
    }
}
