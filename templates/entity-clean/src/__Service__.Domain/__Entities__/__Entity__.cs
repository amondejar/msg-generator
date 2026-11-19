using Msg.Ddd;

namespace {{Service}}.Domain.{{Entities}};

/// <summary>
/// Aggregate root. Created by "msg add entity"; from now on this file is yours: add the methods
/// that express your business operations and protect the rules in <see cref="Apply"/>.
/// </summary>
public sealed class {{Entity}} : AggregateRoot<Guid>
{
    private {{Entity}}()
    {
        // For EF Core. Use Create(...) in your code.
    }

{{AggregateProperties}}

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static {{Entity}} Create({{FactoryParameters}})
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new {{Entity}} { Id = Guid.CreateVersion7(), CreatedAt = now, UpdatedAt = now };
        entity.Apply({{FactoryArguments}});
        entity.Raise(new {{Entity}}Created(entity.Id));
        return entity;
    }

    public void Update({{FactoryParameters}})
    {
        Apply({{FactoryArguments}});
        UpdatedAt = DateTimeOffset.UtcNow;
        Raise(new {{Entity}}Updated(Id));
    }

    public void Delete() => Raise(new {{Entity}}Deleted(Id));

    /// <summary>Business rules: throw <see cref="DomainException"/> when one is broken (the API answers 422).</summary>
    private void Apply({{FactoryParameters}})
    {
{{ApplyGuards}}

{{ApplyAssignments}}
    }
}
