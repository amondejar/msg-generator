using {{Service}}.Domain.{{Entities}};
// @msg:if hasRequiredString
using Msg.Ddd;
// @msg:end

namespace {{Service}}.Api.Tests.Domain;

/// <summary>Domain tests: no database, no HTTP, milliseconds. Add one per business rule.</summary>
public sealed class {{Entity}}Tests
{
    [Fact]
    public void CreateRaisesCreatedEvent()
    {
        var entity = {{Entity}}.Create({{DomainSampleArguments}});

        var created = Assert.IsType<{{Entity}}Created>(Assert.Single(entity.DomainEvents));
        Assert.Equal(entity.Id, created.{{Entity}}Id);
    }

    [Fact]
    public void UpdateRaisesUpdatedEvent()
    {
        var entity = {{Entity}}.Create({{DomainSampleArguments}});
        entity.ClearDomainEvents();

        entity.Update({{DomainSampleArguments}});

        Assert.IsType<{{Entity}}Updated>(Assert.Single(entity.DomainEvents));
    }
    // @msg:if hasRequiredString

    [Fact]
    public void RequiredTextIsEnforced()
    {
        Assert.Throws<DomainException>(() => {{Entity}}.Create({{DomainInvalidArguments}}));
    }
    // @msg:end
}
