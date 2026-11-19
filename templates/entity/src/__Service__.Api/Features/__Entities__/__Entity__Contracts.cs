using System.ComponentModel.DataAnnotations;

namespace {{Service}}.Api.Features.{{Entities}};

// Public contract of the API. Kept separate from the entity so the database can change
// without breaking clients (and clients can't set Id, CreatedAt...).

/// <summary>Body of POST and PUT. Validated automatically (400 ProblemDetails on errors).</summary>
public sealed record {{Entity}}Request(
{{RequestParameters}});

public sealed record {{Entity}}Response(
    Guid Id,
{{ResponseParameters}}
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static {{Entity}}Response From({{Entity}} entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new(
            entity.Id,
{{ResponseArguments}}
            entity.CreatedAt,
            entity.UpdatedAt);
    }
}
