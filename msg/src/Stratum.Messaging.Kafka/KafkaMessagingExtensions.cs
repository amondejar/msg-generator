using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stratum.Messaging.Kafka;

public static class KafkaMessagingExtensions
{
    /// <summary>
    /// Kafka as the broker. Bootstrap servers from ConnectionStrings:kafka (what Aspire injects) or
    /// Messaging:Kafka:BootstrapServers; the rest from the Messaging:Kafka section.
    /// </summary>
    public static MessagingBuilder UseKafka(this MessagingBuilder messaging, IConfiguration configuration, Action<KafkaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(configuration);

        messaging.Services.AddOptions<KafkaOptions>()
            .Bind(configuration.GetSection(KafkaOptions.SectionName))
            .PostConfigure(o =>
            {
                o.BootstrapServers ??= configuration.GetConnectionString(KafkaOptions.ConnectionStringName);
                configure?.Invoke(o);
            });

        messaging.Services.TryAddSingleton<KafkaTransport>();
        messaging.Services.Replace(ServiceDescriptor.Singleton<IMessageTransport>(sp => sp.GetRequiredService<KafkaTransport>()));
        return messaging;
    }
}
