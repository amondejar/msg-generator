using System.Collections.Concurrent;
using System.Globalization;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stratum.Messaging.AzureServiceBus;

/// <summary>
/// Azure Service Bus transport. One topic per integration event; each service consumes through its own subscription
/// (named after the service), so every service gets every event. Messages are completed only after they were
/// handled; failures go to the subscription's dead-letter queue.
/// </summary>
public sealed partial class AzureServiceBusTransport(IOptions<AzureServiceBusOptions> options, ILogger<AzureServiceBusTransport> logger)
    : IMessageTransport, IAsyncDisposable
{
    internal const string SourceProperty = "msg-source";
    internal const string OccurredAtProperty = "msg-occurred-at";
    internal const string TraceParentProperty = "traceparent";

    private readonly Lazy<ServiceBusClient> _client = new(() => new ServiceBusClient(ConnectionString(options.Value)));
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _entities = new(StringComparer.Ordinal);

    public async Task SendAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await EnsureAsync(envelope.Topic, null).ConfigureAwait(false);
        var sender = _senders.GetOrAdd(envelope.Topic, topic => _client.Value.CreateSender(topic));
        await sender.SendMessageAsync(ToMessage(envelope), cancellationToken).ConfigureAwait(false);
    }

    public async Task ConsumeAsync(
        IReadOnlyCollection<string> topics,
        string subscription,
        Func<MessageEnvelope, CancellationToken, Task<ConsumeResult>> onMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentNullException.ThrowIfNull(onMessage);

        var processors = new List<ServiceBusProcessor>();
        try
        {
            foreach (var topic in topics)
            {
                await EnsureAsync(topic, subscription).ConfigureAwait(false);
                var processor = _client.Value.CreateProcessor(topic, subscription, new ServiceBusProcessorOptions
                {
                    AutoCompleteMessages = false,
                    MaxConcurrentCalls = options.Value.MaxConcurrentCalls,
                });

                processor.ProcessMessageAsync += async args =>
                {
                    var result = await onMessage(FromMessage(topic, args.Message), args.CancellationToken).ConfigureAwait(false);
                    if (result == ConsumeResult.DeadLetter)
                    {
                        await args.DeadLetterMessageAsync(args.Message, "msg-handler-failed", "Handler failed every attempt or the message can't be read", args.CancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await args.CompleteMessageAsync(args.Message, args.CancellationToken).ConfigureAwait(false);
                    }
                };

                processor.ProcessErrorAsync += args =>
                {
                    LogError(logger, args.Exception, args.EntityPath, args.ErrorSource.ToString());
                    return Task.CompletedTask;
                };

                await processor.StartProcessingAsync(cancellationToken).ConfigureAwait(false);
                processors.Add(processor);
            }

            LogSubscribed(logger, subscription, string.Join(", ", topics));
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var processor in processors)
            {
                await processor.StopProcessingAsync(CancellationToken.None).ConfigureAwait(false);
                await processor.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Creates the topic (and subscription) if asked to, and never against the emulator.</summary>
    private Task EnsureAsync(string topic, string? subscription)
    {
        var connection = ConnectionString(options.Value);
        if (!options.Value.CreateEntities || connection.Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        return _entities.GetOrAdd($"{topic}|{subscription}", async _ =>
        {
            var admin = new ServiceBusAdministrationClient(connection);
            if (!(await admin.TopicExistsAsync(topic).ConfigureAwait(false)).Value)
            {
                await admin.CreateTopicAsync(topic).ConfigureAwait(false);
            }

            if (subscription is not null && !(await admin.SubscriptionExistsAsync(topic, subscription).ConfigureAwait(false)).Value)
            {
                await admin.CreateSubscriptionAsync(topic, subscription).ConfigureAwait(false);
            }
        });
    }

    internal static ServiceBusMessage ToMessage(MessageEnvelope envelope)
    {
        var message = new ServiceBusMessage(BinaryData.FromString(envelope.Payload))
        {
            MessageId = envelope.MessageId,    // with duplicate detection on, the broker also drops resends
            ContentType = "application/json",
            Subject = envelope.Topic,
            CorrelationId = envelope.CorrelationId,
        };
        message.ApplicationProperties[SourceProperty] = envelope.Source;
        message.ApplicationProperties[OccurredAtProperty] = envelope.OccurredAt.ToString("O", CultureInfo.InvariantCulture);
        if (envelope.TraceParent is { } traceParent)
        {
            message.ApplicationProperties[TraceParentProperty] = traceParent;
        }

        return message;
    }

    internal static MessageEnvelope FromMessage(string topic, ServiceBusReceivedMessage message)
    {
        string? Property(string name) => message.ApplicationProperties.TryGetValue(name, out var value) ? value?.ToString() : null;

        return new MessageEnvelope
        {
            MessageId = message.MessageId,
            Topic = topic,
            Source = Property(SourceProperty) ?? "unknown",
            Payload = message.Body.ToString(),
            OccurredAt = DateTimeOffset.TryParse(Property(OccurredAtProperty), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var occurredAt)
                ? occurredAt
                : message.EnqueuedTime,
            CorrelationId = message.CorrelationId,
            TraceParent = Property(TraceParentProperty),
        };
    }

    private static string ConnectionString(AzureServiceBusOptions options) =>
        string.IsNullOrWhiteSpace(options.ConnectionString)
            ? throw new InvalidOperationException(
                "Azure Service Bus: missing ConnectionStrings:servicebus (Aspire sets it with WithReference) or Messaging:AzureServiceBus:ConnectionString.")
            : options.ConnectionString;

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
        {
            await sender.DisposeAsync().ConfigureAwait(false);
        }

        if (_client.IsValueCreated)
        {
            await _client.Value.DisposeAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Service Bus subscription {Subscription} listening to {Topics}")]
    private static partial void LogSubscribed(ILogger logger, string subscription, string topics);

    [LoggerMessage(Level = LogLevel.Error, Message = "Service Bus error on {EntityPath} ({Source})")]
    private static partial void LogError(ILogger logger, Exception exception, string entityPath, string source);
}
