namespace Stratum.Ddd;

/// <summary>An intention to change state, handled by exactly one <see cref="ICommandHandler{TCommand, TResult}"/>.</summary>
#pragma warning disable CA1040 // Marker interfaces: they carry the result type and enable handler discovery.
public interface ICommand<TResult>;

/// <summary>A request for data that changes nothing, handled by exactly one <see cref="IQueryHandler{TQuery, TResult}"/>.</summary>
public interface IQuery<TResult>;
#pragma warning restore CA1040

/// <summary>
/// Use case. Endpoints receive the handler by dependency injection (no mediator):
/// <c>ICommandHandler&lt;CreateOrder, Guid&gt; handler</c>.
/// </summary>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

/// <summary>Reacts to a domain event. An event can have any number of handlers.</summary>
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}

/// <summary>Delivers a domain event to every registered handler of its runtime type.</summary>
public interface IDomainEventPublisher
{
    Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken);
}
