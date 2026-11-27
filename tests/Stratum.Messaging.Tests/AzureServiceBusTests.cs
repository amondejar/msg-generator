using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratum.Messaging.AzureServiceBus;

namespace Stratum.Messaging.Tests;

/// <summary>Without a broker: the envelope survives the trip through a Service Bus message, and the configuration is read like Aspire sets it.</summary>
public sealed class AzureServiceBusTests
{
    [Fact]
    public void EnvelopeRoundTripsThroughAServiceBusMessage()
    {
        var envelope = new MessageEnvelope
        {
            MessageId = Guid.CreateVersion7().ToString(),
            Topic = "orders.order-created",
            Source = "orders",
            Payload = """{"orderId":"1","total":5}""",
            OccurredAt = new DateTimeOffset(2026, 11, 20, 13, 30, 0, TimeSpan.Zero),
            CorrelationId = "abc",
            TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
        };

        var sent = AzureServiceBusTransport.ToMessage(envelope);
        var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: sent.Body,
            messageId: sent.MessageId,
            correlationId: sent.CorrelationId,
            properties: sent.ApplicationProperties,
            enqueuedTime: DateTimeOffset.UtcNow);

        Assert.Equal(envelope, AzureServiceBusTransport.FromMessage(envelope.Topic, received));
    }

    [Fact]
    public void ConnectionStringComesFromAspire()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:servicebus"] = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;",
        });
        builder.AddStratumMessaging("orders").UseAzureServiceBus(builder.Configuration);
        using var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptions<AzureServiceBusOptions>>().Value;

        Assert.Contains("UseDevelopmentEmulator=true", options.ConnectionString, StringComparison.Ordinal);
        Assert.IsType<AzureServiceBusTransport>(host.Services.GetRequiredService<IMessageTransport>());
    }

    [Theory]
    [InlineData("Endpoint=sb://shop.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey=abc=", null)]
    [InlineData("Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;", null)]
    [InlineData("Endpoint=sb://shop.servicebus.windows.net/", "shop.servicebus.windows.net")]
    [InlineData("https://shop.servicebus.windows.net:443/", "shop.servicebus.windows.net")]
    [InlineData("shop.servicebus.windows.net", "shop.servicebus.windows.net")]
    public void WithoutAKeyTheServiceSignsInWithItsIdentity(string connection, string? expected) =>
        Assert.Equal(expected, AzureServiceBusTransport.Namespace(connection));
}
