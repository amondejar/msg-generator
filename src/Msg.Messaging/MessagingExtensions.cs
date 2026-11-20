using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Msg.Messaging;

public static class MessagingExtensions
{
    /// <summary>
    /// Integration events between services: <see cref="IEventBus"/> (outbox), the outbox dispatcher and the
    /// consumers of the registered handlers. Choose a broker (UseInMemory, Msg.Messaging.Kafka,
    /// Msg.Messaging.AzureServiceBus) and, with a database, Msg.Messaging.EntityFrameworkCore for a transactional outbox.
    /// </summary>
    /// <param name="serviceName">Source of published events and subscription name (configurable in Messaging:ServiceName).</param>
    public static MessagingBuilder AddMsgMessaging(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var services = builder.Services;
        services.AddOptions<MessagingOptions>()
            .Configure(o => o.ServiceName = serviceName)
            .BindConfiguration(MessagingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IEventBus, OutboxEventBus>();

        // Outbox and inbox in memory unless a database takes over (UseEntityFramework): without a database the
        // outbox doesn't survive a restart.
        services.TryAddSingleton<InMemoryOutbox>();
        services.TryAddScoped<IOutboxWriter>(sp => sp.GetRequiredService<InMemoryOutbox>());
        services.TryAddScoped<IOutboxStore>(sp => sp.GetRequiredService<InMemoryOutbox>());
        services.TryAddSingleton<IInbox, InMemoryInbox>();

        var registry = services.Select(d => d.ImplementationInstance).OfType<HandlerRegistry>().FirstOrDefault();
        if (registry is null)
        {
            registry = new HandlerRegistry();
            services.AddSingleton(registry);
            services.AddSingleton<OutboxDispatcher>();
            services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
            services.AddSingleton<IntegrationEventConsumer>();
            services.AddHostedService(sp => sp.GetRequiredService<IntegrationEventConsumer>());
        }

        return new MessagingBuilder(services, registry);
    }
}

/// <summary>Fluent configuration returned by AddMsgMessaging.</summary>
public sealed class MessagingBuilder(IServiceCollection services, HandlerRegistry registry)
{
    public IServiceCollection Services { get; } = services;

    /// <summary>
    /// In-process broker: events don't leave this process (tests, or one service alone). The outbox and inbox are
    /// in memory too, unless Msg.Messaging.EntityFrameworkCore puts them in the database.
    /// </summary>
    public MessagingBuilder UseInMemory()
    {
        Services.TryAddSingleton<InMemoryTransport>();
        Services.Replace(ServiceDescriptor.Singleton<IMessageTransport>(sp => sp.GetRequiredService<InMemoryTransport>()));
        return this;
    }

    public MessagingBuilder AddHandler<TEvent, THandler>()
        where TEvent : class, IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        if (registry.Add(typeof(TEvent), typeof(THandler)))
        {
            Services.TryAddScoped<THandler>();
        }

        return this;
    }

    /// <summary>Every IIntegrationEventHandler&lt;T&gt; and IIntegrationEventMapper in the assembly.</summary>
    public MessagingBuilder AddFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }))
        {
            foreach (var contract in type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>)))
            {
                if (registry.Add(contract.GetGenericArguments()[0], type))
                {
                    Services.TryAddScoped(type);
                }
            }

            if (typeof(IIntegrationEventMapper).IsAssignableFrom(type))
            {
                Services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IIntegrationEventMapper), type));
            }
        }

        return this;
    }

    public MessagingBuilder Configure(Action<MessagingOptions> configure)
    {
        Services.PostConfigure(configure);
        return this;
    }
}

internal static class MessagingServices
{
    public static IMessageTransport Transport(IServiceProvider services) =>
        services.GetService<IMessageTransport>()
        ?? throw new InvalidOperationException(
            "Messaging: no broker configured. Call UseInMemory(), UseKafka(...) (Msg.Messaging.Kafka) or " +
            "UseAzureServiceBus(...) (Msg.Messaging.AzureServiceBus) after AddMsgMessaging.");
}
