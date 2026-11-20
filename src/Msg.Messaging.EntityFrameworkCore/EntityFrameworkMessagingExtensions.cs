using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Msg.Messaging.EntityFrameworkCore;

public static class EntityFrameworkMessagingExtensions
{
    /// <summary>
    /// Outbox and inbox in <typeparamref name="TContext"/>'s database (tables from <c>modelBuilder.AddMsgMessaging()</c>).
    /// Domain events become integration events through <see cref="IntegrationEventsInterceptor"/>, registered as an
    /// <see cref="IInterceptor"/> service: add <c>options.AddInterceptors(sp.GetServices&lt;IInterceptor&gt;())</c> to the DbContext.
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

        // Picked up by DbContexts that add the IInterceptor services (Stratum's clean + DDD services do), and in
        // registration order: registered before the domain events interceptor, it sees the events first.
        services.AddScoped<IInterceptor>(sp => sp.GetRequiredService<IntegrationEventsInterceptor>());
        return messaging;
    }
}
