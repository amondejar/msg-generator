using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Msg.Messaging.EntityFrameworkCore;

public static class EntityFrameworkMessagingExtensions
{
    /// <summary>
    /// Outbox and inbox in <typeparamref name="TContext"/>'s database (tables from <c>modelBuilder.AddMsgMessaging()</c>).
    /// Add <see cref="IntegrationEventsInterceptor"/> to the DbContext to publish domain events as integration events.
    /// </summary>
    public static MessagingBuilder UseEntityFramework<TContext>(this MessagingBuilder messaging)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(messaging);

        var services = messaging.Services;
        services.Replace(ServiceDescriptor.Scoped<IOutboxWriter, EfOutbox<TContext>>());
        services.Replace(ServiceDescriptor.Scoped<IOutboxStore, EfOutbox<TContext>>());
        services.Replace(ServiceDescriptor.Scoped<IInbox, EfInbox<TContext>>());
        services.TryAddScoped<IntegrationEventsInterceptor>();
        return messaging;
    }
}
