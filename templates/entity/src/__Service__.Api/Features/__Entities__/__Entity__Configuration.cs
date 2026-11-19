using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace {{Service}}.Api.Features.{{Entities}};

/// <summary>Database mapping for <see cref="{{Entity}}"/>. Applied automatically by the DbContext.</summary>
public sealed class {{Entity}}Configuration : IEntityTypeConfiguration<{{Entity}}>
{
    public void Configure(EntityTypeBuilder<{{Entity}}> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("{{entities_snake}}");
        builder.HasKey(e => e.Id);
{{PropertyConfigurations}}
    }
}
