using {{Service}}.Domain.{{Entities}};
using Stratum.Ddd;

namespace {{Service}}.Application.{{Entities}};

// Domain event handlers run after the change is committed. Put side effects here:
// update a read model, send a notification, publish an integration event for other services...
// Delete the ones you don't need. Any number of handlers can react to the same event.

public sealed class {{Entity}}CreatedHandler : IDomainEventHandler<{{Entity}}Created>
{
    public Task HandleAsync({{Entity}}Created domainEvent, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class {{Entity}}UpdatedHandler : IDomainEventHandler<{{Entity}}Updated>
{
    public Task HandleAsync({{Entity}}Updated domainEvent, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class {{Entity}}DeletedHandler : IDomainEventHandler<{{Entity}}Deleted>
{
    public Task HandleAsync({{Entity}}Deleted domainEvent, CancellationToken cancellationToken) => Task.CompletedTask;
}
