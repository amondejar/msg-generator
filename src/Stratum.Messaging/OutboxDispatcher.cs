using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stratum.Messaging;

/// <summary>
/// Sends the pending outbox events to the broker, in order of occurrence, with growing waits after failures.
/// At-least-once: if the process stops between sending and marking as sent, the event is sent again
/// (consumers ignore it thanks to the inbox).
/// </summary>
public sealed partial class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IServiceProvider services,
    IOptions<MessagingOptions> options,
    TimeProvider time,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    /// <summary>Events given up after <see cref="MessagingOptions.OutboxMaxAttempts"/> wait here, with their LastError.</summary>
    internal static readonly DateTime GivenUp = new(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Sends one batch of pending events. Public so tests can flush the outbox without waiting.</summary>
    /// <returns>How many were sent.</returns>
    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var transport = MessagingServices.Transport(services);

        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var pending = await store.FetchPendingAsync(settings.OutboxBatchSize, time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return 0;
        }

        var sent = 0;
        var broker = MessagingServices.BrokerOf(transport);
        foreach (var message in pending)
        {
            using var activity = MessagingTelemetry.StartSend(message, broker);
            try
            {
                await transport.SendAsync(
                    new MessageEnvelope
                    {
                        MessageId = message.Id.ToString(),
                        Topic = message.Topic,
                        Source = settings.ServiceName,
                        Payload = message.Payload,
                        OccurredAt = new DateTimeOffset(DateTime.SpecifyKind(message.OccurredAt, DateTimeKind.Utc)),
                        CorrelationId = message.CorrelationId,
                        TraceParent = message.TraceParent,
                    },
                    cancellationToken).ConfigureAwait(false);

                message.SentAt = time.GetUtcNow().UtcDateTime;
                message.LastError = null;
                MessagingTelemetry.Sent(message.Topic, ok: true);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
                MessagingTelemetry.Sent(message.Topic, ok: false);

                if (message.Attempts >= settings.OutboxMaxAttempts)
                {
                    message.NextAttemptAt = GivenUp;
                    LogGivenUp(logger, ex, message.Topic, message.Id, message.Attempts);
                }
                else
                {
                    message.NextAttemptAt = time.GetUtcNow().UtcDateTime + settings.BackoffFor(message.Attempts) * 5;
                    LogSendFailed(logger, ex, message.Topic, message.Id, message.Attempts);
                }

                // Keep the order: later events of this batch wait for the next round.
                break;
            }
        }

        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return sent;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.OutboxDispatcherEnabled)
        {
            return;
        }

        var nextCleanup = time.GetUtcNow() + TimeSpan.FromMinutes(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (time.GetUtcNow() >= nextCleanup)
            {
                nextCleanup = time.GetUtcNow() + options.Value.CleanupInterval;
                await CleanupAsync(stoppingToken).ConfigureAwait(false);
            }

            var sent = 0;
            try
            {
                sent = await DispatchPendingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // E.g. the database isn't ready yet: try again on the next round.
                LogDispatchFailed(logger, ex);
            }

            if (sent < options.Value.OutboxBatchSize)
            {
                try
                {
                    await Task.Delay(options.Value.OutboxPollInterval, time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Deletes sent outbox messages and journal rows older than <see cref="MessagingOptions.TraceRetention"/>.
    /// Public so tests can run it without waiting.
    /// </summary>
    /// <returns>How many rows were deleted.</returns>
    public async Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IMessageJournal>() is not { } journal)
            {
                return 0;
            }

            var removed = await journal.PurgeAsync(time.GetUtcNow().UtcDateTime - options.Value.TraceRetention, cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                LogCleaned(logger, removed);
            }

            return removed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogCleanupFailed(logger, ex);
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} old messages (sent outbox and msg_received)")]
    private static partial void LogCleaned(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete old messages; trying again later")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send {Topic} {MessageId} (attempt {Attempts}); it will be retried")]
    private static partial void LogSendFailed(ILogger logger, Exception exception, string topic, Guid messageId, int attempts);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Gave up sending {Topic} {MessageId} after {Attempts} attempts: see LastError in the outbox table")]
    private static partial void LogGivenUp(ILogger logger, Exception exception, string topic, Guid messageId, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox dispatch failed; retrying on the next round")]
    private static partial void LogDispatchFailed(ILogger logger, Exception exception);
}
