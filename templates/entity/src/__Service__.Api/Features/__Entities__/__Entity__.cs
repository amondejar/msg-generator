namespace {{Service}}.Api.Features.{{Entities}};

/// <summary>Created by "msg add entity". This file is yours: MSG won't overwrite it.</summary>
public sealed class {{Entity}}
{
    /// <summary>Time-ordered UUID v7: good index locality, safe to expose in URLs.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

{{EntityProperties}}

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
