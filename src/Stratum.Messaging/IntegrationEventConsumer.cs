using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stratum.Messaging;

/// <summary>
/// Receives the events of the registered handlers and runs each handler once per event (inbox), in its own
/// DI scope, retrying with growing waits. After <see cref="MessagingOptions.MaxHandlerAttempts"/> the message
/// goes to the dead-letter queue.
/// </summary>
public sealed partial class IntegrationEventConsumer(
    IServiceScopeFactory scopes,
    IServiceProvider services,
    HandlerRegistry registry,
    IOptions<MessagingOptions> options,
    TimeProvider time,
    ILogger<IntegrationEventConsumer> logger) : BackgroundService
{
    /// <summary>Handles one message. Public so transports and tests can use it directly.</summary>
    public async Task<ConsumeResult> HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var settings = options.Value;
        var receivedAt = time.GetUtcNow().UtcDateTime;

        foreach (var handler in registry.For(envelope.Topic))
        {
            object? integrationEvent;
            try
            {
                integrationEvent = JsonSerializer.Deserialize(envelope.Payload, handler.EventType, EventTopology.JsonOptions);
            }
            catch (JsonException ex)
            {
                LogUnreadable(logger, ex, envelope.Topic, envelope.MessageId);
                MessagingTelemetry.Processed(envelope.Topic, handler.Consumer, ReceivedStatus.Unreadable, 0);
                await JournalAsync(envelope, handler.Consumer, ReceivedStatus.Unreadable, 1, 0, ex.Message, receivedAt, cancellationToken).ConfigureAwait(false);
                return ConsumeResult.DeadLetter;
            }

            if (integrationEvent is null)
            {
                await JournalAsync(envelope, handler.Consumer, ReceivedStatus.Unreadable, 1, 0, "Empty payload (null).", receivedAt, cancellationToken).ConfigureAwait(false);
                return ConsumeResult.DeadLetter;
            }

            for (var attempt = 1; ; attempt++)
            {
                using var activity = MessagingTelemetry.StartProcess(envelope, handler.Consumer);
                var started = Stopwatch.GetTimestamp();
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var inbox = scope.ServiceProvider.GetRequiredService<IInbox>();
                    var instance = scope.ServiceProvider.GetRequiredService(handler.HandlerType);
                    var context = new MessageContext(envelope.MessageId, envelope.Topic, envelope.Source, envelope.CorrelationId, attempt);

                    var ran = await inbox.ExecuteOnceAsync(
                        envelope.MessageId,
                        handler.Consumer,
                        ct => handler.Invoker(instance, integrationEvent, context, ct),
                        cancellationToken).ConfigureAwait(false);

                    var elapsed = Elapsed(started);
                    if (!ran)
                    {
                        activity?.SetTag("msg.duplicate", true);
                        activity?.SetTag("stratum.messaging.outcome", "duplicate");
                        LogDuplicate(logger, envelope.Topic, envelope.MessageId, handler.Consumer);
                    }
                    else
                    {
                        activity?.SetTag("stratum.messaging.outcome", ReceivedStatus.Processed);
                        MessagingTelemetry.Processed(envelope.Topic, handler.Consumer, ReceivedStatus.Processed, elapsed);
                        await JournalAsync(envelope, handler.Consumer, ReceivedStatus.Processed, attempt, elapsed, null, receivedAt, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    var elapsed = Elapsed(started);
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity?.AddException(ex);
                    if (attempt >= settings.MaxHandlerAttempts)
                    {
                        activity?.SetTag("stratum.messaging.outcome", ReceivedStatus.DeadLettered);
                        LogDeadLetter(logger, ex, envelope.Topic, envelope.MessageId, handler.Consumer, attempt);
                        MessagingTelemetry.Processed(envelope.Topic, handler.Consumer, ReceivedStatus.DeadLettered, elapsed);
                        await JournalAsync(envelope, handler.Consumer, ReceivedStatus.DeadLettered, attempt, elapsed, Describe(ex), receivedAt, cancellationToken).ConfigureAwait(false);
                        return ConsumeResult.DeadLetter;
                    }

                    activity?.SetTag("stratum.messaging.outcome", ReceivedStatus.Retrying);
                    LogRetry(logger, ex, envelope.Topic, envelope.MessageId, handler.Consumer, attempt);
                    MessagingTelemetry.Processed(envelope.Topic, handler.Consumer, ReceivedStatus.Retrying, elapsed);
                    await JournalAsync(envelope, handler.Consumer, ReceivedStatus.Retrying, attempt, elapsed, Describe(ex), receivedAt, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(settings.BackoffFor(attempt), time, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return ConsumeResult.Completed;
    }

    private static int Elapsed(long started) => (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    private static string Describe(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        return text.Length > 2000 ? text[..2000] : text;
    }

    private int _journalWarned;

    /// <summary>Writes the journal row in its own scope: a failure here (e.g. msg_received not migrated yet) never stops consuming.</summary>
    private async Task JournalAsync(
        MessageEnvelope envelope, string consumer, string status, int attempts, int durationMs, string? error, DateTime receivedAt, CancellationToken cancellationToken)
    {
        if (!options.Value.JournalEnabled)
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IMessageJournal>() is not { } journal)
            {
                return;
            }

            await journal.RecordAsync(
                new ReceivedMessage
                {
                    MessageId = envelope.MessageId,
                    Consumer = consumer,
                    Topic = envelope.Topic,
                    Source = envelope.Source,
                    CorrelationId = envelope.CorrelationId,
                    TraceId = MessagingTelemetry.TraceIdOf(envelope.TraceParent),
                    ReceivedAt = receivedAt,
                    UpdatedAt = time.GetUtcNow().UtcDateTime,
                    Status = status,
                    Attempts = attempts,
                    DurationMs = durationMs,
                    LastError = error,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (Interlocked.Exchange(ref _journalWarned, 1) == 0)
            {
                LogJournalFailed(logger, ex);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ConsumersEnabled || registry.Handlers.Count == 0)
        {
            return;
        }

        var transport = MessagingServices.Transport(services);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await transport.ConsumeAsync(registry.Topics, options.Value.ServiceName, HandleAsync, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Broker not reachable yet (e.g. still starting in Aspire): reconnect.
                LogConsumeFailed(logger, ex);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Topic} {MessageId} can't be read as the expected event: moved to dead-letter")]
    private static partial void LogUnreadable(ILogger logger, Exception exception, string topic, string messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Topic} {MessageId} already handled by {Consumer}: ignored")]
    private static partial void LogDuplicate(ILogger logger, string topic, string messageId, string consumer);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Consumer} failed on {Topic} {MessageId} (attempt {Attempt}); retrying")]
    private static partial void LogRetry(ILogger logger, Exception exception, string topic, string messageId, string consumer, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Consumer} failed on {Topic} {MessageId} {Attempt} times: moved to dead-letter")]
    private static partial void LogDeadLetter(ILogger logger, Exception exception, string topic, string messageId, string consumer, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the journal of received messages (msg_received): is its migration applied? Messages are still handled; Stratum Studio's event traces won't show them")]
    private static partial void LogJournalFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Lost the connection to the broker; reconnecting in 5 s")]
    private static partial void LogConsumeFailed(ILogger logger, Exception exception);
}
