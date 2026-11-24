using {{Service}}.Domain.{{Entities}};
using Microsoft.EntityFrameworkCore;

namespace {{Service}}.Infrastructure.Persistence.{{Entities}};

public sealed class {{Entity}}Repository({{Service}}DbContext db) : I{{Entity}}Repository
{
    public Task<{{Entity}}?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.{{Entities}}.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<{{Entity}}> Items, int Total)> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var total = await db.{{Entities}}.CountAsync(cancellationToken);
        var items = await db.{{Entities}}
            .AsNoTracking()
            .OrderBy(e => e.Id)   // UUID v7 → creation order, stable pagination
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public void Add({{Entity}} entity) => db.{{Entities}}.Add(entity);

    public void Remove({{Entity}} entity) => db.{{Entities}}.Remove(entity);
}
