using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratum.Messaging.Kafka;

namespace Stratum.Messaging.Tests;

/// <summary>Without a broker: the envelope survives the trip through a Kafka message, and the configuration is read like Aspire sets it.</summary>
public sealed class KafkaTests
{
    [Fact]
    public void EnvelopeRoundTripsThroughAKafkaMessage()
    {
        var envelope = new MessageEnvelope
        {
            MessageId = Guid.CreateVersion7().ToString(),
            Topic = "orders.order-created",
            Source = "orders",
            Payload = """{"orderId":"1","total":5}""",
            OccurredAt = new DateTimeOffset(2026, 11, 20, 7, 30, 0, TimeSpan.Zero),
            CorrelationId = "abc",
            TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
        };

        var back = KafkaTransport.FromMessage(envelope.Topic, KafkaTransport.ToMessage(envelope));

        Assert.Equal(envelope, back);
    }

    [Fact]
    public void BootstrapServersComeFromTheAspireConnectionString()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:kafka"] = "localhost:49152",
            ["Messaging:Kafka:Partitions"] = "3",
        });
        builder.AddStratumMessaging("orders").UseKafka(builder.Configuration);
        using var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptions<KafkaOptions>>().Value;

        Assert.Equal("localhost:49152", options.BootstrapServers);
        Assert.Equal(3, options.Partitions);
        Assert.IsType<KafkaTransport>(host.Services.GetRequiredService<IMessageTransport>());
    }
}
