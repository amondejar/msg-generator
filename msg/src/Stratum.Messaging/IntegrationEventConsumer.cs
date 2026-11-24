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
                return ConsumeResult.DeadLetter;
            }

            if (integrationEvent is null)
            {
                return ConsumeResult.DeadLetter;
            }

            for (var attempt = 1; ; attempt++)
            {
                using var activity = MessagingTelemetry.StartProcess(envelope, handler.Consumer);
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

                    if (!ran)
                    {
                        activity?.SetTag("msg.duplicate", true);
                        LogDuplicate(logger, envelope.Topic, envelope.MessageId, handler.Consumer);
                    }

                    break;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    if (attempt >= settings.MaxHandlerAttempts)
                    {
                        LogDeadLetter(logger, ex, envelope.Topic, envelope.MessageId, handler.Consumer, attempt);
                        return ConsumeResult.DeadLetter;
                    }

                    LogRetry(logger, ex, envelope.Topic, envelope.MessageId, handler.Consumer, attempt);
                    await Task.Delay(settings.BackoffFor(attempt), time, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return ConsumeResult.Completed;
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Lost the connection to the broker; reconnecting in 5 s")]
    private static partial void LogConsumeFailed(ILogger logger, Exception exception);
}
