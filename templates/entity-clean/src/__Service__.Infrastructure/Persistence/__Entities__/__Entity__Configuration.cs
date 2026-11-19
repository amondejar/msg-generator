using {{Service}}.Domain.{{Entities}};
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace {{Service}}.Infrastructure.Persistence.{{Entities}};

/// <summary>Database mapping for the <see cref="{{Entity}}"/> aggregate. Applied automatically by the DbContext.</summary>
public sealed class {{Entity}}Configuration : IEntityTypeConfiguration<{{Entity}}>
{
    public void Configure(EntityTypeBuilder<{{Entity}}> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("{{entities_snake}}");
        builder.HasKey(e => e.Id);
        builder.Ignore(e => e.DomainEvents);
{{PropertyConfigurations}}
    }
}
