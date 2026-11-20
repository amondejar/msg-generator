using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stratum.Messaging.AzureServiceBus;

public static class AzureServiceBusMessagingExtensions
{
    /// <summary>
    /// Azure Service Bus as the broker. Connection string from ConnectionStrings:servicebus (what Aspire injects,
    /// also for the emulator) or Messaging:AzureServiceBus:ConnectionString.
    /// </summary>
    public static MessagingBuilder UseAzureServiceBus(this MessagingBuilder messaging, IConfiguration configuration, Action<AzureServiceBusOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(configuration);

        messaging.Services.AddOptions<AzureServiceBusOptions>()
            .Bind(configuration.GetSection(AzureServiceBusOptions.SectionName))
            .PostConfigure(o =>
            {
                o.ConnectionString ??= configuration.GetConnectionString(AzureServiceBusOptions.ConnectionStringName);
                configure?.Invoke(o);
            });

        messaging.Services.TryAddSingleton<AzureServiceBusTransport>();
        messaging.Services.Replace(ServiceDescriptor.Singleton<IMessageTransport>(sp => sp.GetRequiredService<AzureServiceBusTransport>()));
        return messaging;
    }
}
