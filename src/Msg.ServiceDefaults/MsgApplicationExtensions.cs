using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Msg.Ddd;

namespace Msg.ServiceDefaults;

public static class MsgApplicationExtensions
{
    private static readonly Type[] HandlerContracts =
    [
        typeof(ICommandHandler<,>),
        typeof(IQueryHandler<,>),
        typeof(IDomainEventHandler<>),
    ];

    /// <summary>
    /// Registers every command, query and domain event handler found in <paramref name="assemblies"/>
    /// (scoped), plus the <see cref="IDomainEventPublisher"/>. No mediator: endpoints inject the handler they need.
    /// </summary>
    public static IServiceCollection AddMsgApplication(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        services.TryAddScoped<IDomainEventPublisher, DomainEventPublisher>();

        var handlers =
            from type in assemblies.SelectMany(a => a.DefinedTypes)
            where type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
            from contract in type.ImplementedInterfaces
            where contract.IsGenericType && HandlerContracts.Contains(contract.GetGenericTypeDefinition())
            select (contract, type.AsType());

        foreach (var (contract, implementation) in handlers)
        {
            services.TryAddEnumerable(ServiceDescriptor.Scoped(contract, implementation));
        }

        return services;
    }
}

/// <summary>Calls every <see cref="IDomainEventHandler{TEvent}"/> registered for the event's runtime type, in order.</summary>
internal sealed class DomainEventPublisher(IServiceProvider services) : IDomainEventPublisher
{
    private delegate Task Invoker(IServiceProvider services, IDomainEvent domainEvent, CancellationToken cancellationToken);

    private static readonly ConcurrentDictionary<Type, Invoker> Invokers = new();

    public Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return Invokers.GetOrAdd(domainEvent.GetType(), CreateInvoker)(services, domainEvent, cancellationToken);
    }

    private static Invoker CreateInvoker(Type eventType) =>
        typeof(DomainEventPublisher)
            .GetMethod(nameof(PublishTyped), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(eventType)
            .CreateDelegate<Invoker>();

    private static async Task PublishTyped<TEvent>(IServiceProvider services, IDomainEvent domainEvent, CancellationToken cancellationToken)
        where TEvent : IDomainEvent
    {
        foreach (var handler in services.GetServices<IDomainEventHandler<TEvent>>())
        {
            await handler.HandleAsync((TEvent)domainEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary><see cref="DomainException"/> → 422 Unprocessable Entity, with the rule's message as <c>detail</c>.</summary>
internal sealed class DomainExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DomainException domainException)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "A business rule was violated.",
                Detail = domainException.Message,
            },
        }).ConfigureAwait(false);
    }
}
