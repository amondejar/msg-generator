namespace {{Service}}.Domain.{{Entities}};

/// <summary>Collection-like access to <see cref="{{Entity}}"/> aggregates. Implemented in Infrastructure.</summary>
public interface I{{Entity}}Repository
{
    Task<{{Entity}}?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<(IReadOnlyList<{{Entity}}> Items, int Total)> ListAsync(int page, int pageSize, CancellationToken cancellationToken);

    void Add({{Entity}} entity);

    void Remove({{Entity}} entity);
}
