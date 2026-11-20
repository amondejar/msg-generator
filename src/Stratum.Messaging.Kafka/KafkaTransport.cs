using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stratum.Messaging.Kafka;

/// <summary>
/// Kafka transport. The event JSON is the message value; Stratum's metadata travels in headers (msg-id, msg-source,
/// msg-occurred-at, msg-correlation-id, traceparent). Each service consumes with its own consumer group, so
/// every service gets every event; offsets are committed only after the message was handled (at-least-once).
/// </summary>
public sealed partial class KafkaTransport(IOptions<KafkaOptions> options, ILogger<KafkaTransport> logger) : IMessageTransport, IDisposable
{
    internal const string IdHeader = "msg-id";
    internal const string SourceHeader = "msg-source";
    internal const string OccurredAtHeader = "msg-occurred-at";
    internal const string CorrelationHeader = "msg-correlation-id";
    internal const string TraceParentHeader = "traceparent";
    internal const string ErrorHeader = "msg-dead-letter-reason";

    private readonly Lazy<IProducer<string, string>> _producer = new(() => new ProducerBuilder<string, string>(new ProducerConfig
    {
        BootstrapServers = BootstrapServers(options.Value),
        Acks = Acks.All,
        EnableIdempotence = true,   // no duplicates or reordering caused by the producer's own retries
    }).Build());

    private readonly ConcurrentDictionary<string, Task> _topics = new(StringComparer.Ordinal);

    public async Task SendAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await EnsureTopicsAsync([envelope.Topic]).ConfigureAwait(false);
        await _producer.Value.ProduceAsync(envelope.Topic, ToMessage(envelope), cancellationToken).ConfigureAwait(false);
    }

    public async Task ConsumeAsync(
        IReadOnlyCollection<string> topics,
        string subscription,
        Func<MessageEnvelope, CancellationToken, Task<ConsumeResult>> onMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentNullException.ThrowIfNull(onMessage);

        await EnsureTopicsAsync(topics).ConfigureAwait(false);

        // Consume() blocks: run the loop on its own thread.
        await Task.Factory.StartNew(
            () => ConsumeLoop(topics, subscription, onMessage, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).ConfigureAwait(false);
    }

    private void ConsumeLoop(
        IReadOnlyCollection<string> topics,
        string subscription,
        Func<MessageEnvelope, CancellationToken, Task<ConsumeResult>> onMessage,
        CancellationToken cancellationToken)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = BootstrapServers(options.Value),
            GroupId = subscription,
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,   // a new service also gets the events published before it existed
            AllowAutoCreateTopics = options.Value.CreateTopics,
        }).Build();

        consumer.Subscribe(topics);
        LogSubscribed(logger, subscription, string.Join(", ", topics));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var record = consumer.Consume(cancellationToken);
                if (record?.Message is null)
                {
                    continue;
                }

                var envelope = FromMessage(record.Topic, record.Message);
                var result = onMessage(envelope, cancellationToken).GetAwaiter().GetResult();
                if (result == Stratum.Messaging.ConsumeResult.DeadLetter)
                {
                    SendToDeadLetter(record, "handler failed or the message can't be read");
                }

                consumer.Commit(record);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            consumer.Close();   // leaves the group cleanly: partitions are reassigned right away
        }
    }

    private void SendToDeadLetter(ConsumeResult<string, string> record, string reason)
    {
        var topic = record.Topic + options.Value.DeadLetterSuffix;
        EnsureTopicsAsync([topic]).GetAwaiter().GetResult();

        var headers = new Headers();
        foreach (var header in record.Message.Headers ?? new Headers())
        {
            headers.Add(header.Key, header.GetValueBytes());
        }

        headers.Add(ErrorHeader, Encoding.UTF8.GetBytes(reason));
        _producer.Value.ProduceAsync(topic, new Message<string, string> { Key = record.Message.Key, Value = record.Message.Value, Headers = headers })
            .GetAwaiter().GetResult();
        LogDeadLettered(logger, record.Topic, topic);
    }

    private Task EnsureTopicsAsync(IEnumerable<string> topics)
    {
        if (!options.Value.CreateTopics)
        {
            return Task.CompletedTask;
        }

        return Task.WhenAll(topics.Select(topic => _topics.GetOrAdd(topic, CreateTopicAsync)));
    }

    private async Task CreateTopicAsync(string topic)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers(options.Value) }).Build();
        try
        {
            await admin.CreateTopicsAsync(
            [
                new TopicSpecification
                {
                    Name = topic,
                    NumPartitions = options.Value.Partitions,
                    ReplicationFactor = options.Value.ReplicationFactor,
                },
            ]).ConfigureAwait(false);
            LogTopicCreated(logger, topic);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.TopicAlreadyExists or ErrorCode.NoError))
        {
            // Created by another instance or service.
        }
        catch
        {
            _topics.TryRemove(topic, out _);   // try again next time (e.g. the broker was still starting)
            throw;
        }
    }

    internal static Message<string, string> ToMessage(MessageEnvelope envelope)
    {
        var headers = new Headers
        {
            { IdHeader, Encoding.UTF8.GetBytes(envelope.MessageId) },
            { SourceHeader, Encoding.UTF8.GetBytes(envelope.Source) },
            { OccurredAtHeader, Encoding.UTF8.GetBytes(envelope.OccurredAt.ToString("O", CultureInfo.InvariantCulture)) },
        };

        if (envelope.CorrelationId is { } correlation)
        {
            headers.Add(CorrelationHeader, Encoding.UTF8.GetBytes(correlation));
        }

        if (envelope.TraceParent is { } traceParent)
        {
            headers.Add(TraceParentHeader, Encoding.UTF8.GetBytes(traceParent));
        }

        return new Message<string, string> { Key = envelope.MessageId, Value = envelope.Payload, Headers = headers };
    }

    internal static MessageEnvelope FromMessage(string topic, Message<string, string> message)
    {
        string? Header(string name) =>
            message.Headers is { } headers && headers.TryGetLastBytes(name, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

        return new MessageEnvelope
        {
            MessageId = Header(IdHeader) ?? message.Key ?? string.Empty,
            Topic = topic,
            Source = Header(SourceHeader) ?? "unknown",
            Payload = message.Value,
            OccurredAt = DateTimeOffset.TryParse(Header(OccurredAtHeader), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var occurredAt)
                ? occurredAt
                : message.Timestamp.UtcDateTime,
            CorrelationId = Header(CorrelationHeader),
            TraceParent = Header(TraceParentHeader),
        };
    }

    private static string BootstrapServers(KafkaOptions options) =>
        string.IsNullOrWhiteSpace(options.BootstrapServers)
            ? throw new InvalidOperationException(
                "Kafka: missing ConnectionStrings:kafka (Aspire sets it with WithReference) or Messaging:Kafka:BootstrapServers.")
            : options.BootstrapServers;

    public void Dispose()
    {
        if (_producer.IsValueCreated)
        {
            _producer.Value.Flush(TimeSpan.FromSeconds(5));
            _producer.Value.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Kafka consumer group {Group} subscribed to {Topics}")]
    private static partial void LogSubscribed(ILogger logger, string group, string topics);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kafka topic {Topic} created")]
    private static partial void LogTopicCreated(ILogger logger, string topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message of {Topic} moved to {DeadLetterTopic}")]
    private static partial void LogDeadLettered(ILogger logger, string topic, string deadLetterTopic);
}
