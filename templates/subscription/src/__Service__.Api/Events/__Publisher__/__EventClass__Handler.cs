// @stratum:if subscriptionHandler
// @stratum:if verticalSlice
using {{Service}}.IntegrationEvents.{{Publisher}};
using Stratum.Messaging;

namespace {{Service}}.Api.Events.{{Publisher}};

/// <summary>
/// What this service does when {{Publisher}} publishes {{EventClass}} (topic {{Topic}}). Created once by Stratum:
/// this file is yours. It runs once per event (inbox); if it throws, it's retried and then goes to dead-letter.
/// Inject what you need (the DbContext...): what you save here is committed together with the inbox record.
/// </summary>
public sealed partial class {{EventClass}}Handler(ILogger<{{EventClass}}Handler> logger) : IIntegrationEventHandler<{{EventClass}}>
{
    public Task HandleAsync({{EventClass}} integrationEvent, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        ArgumentNullException.ThrowIfNull(context);

        LogReceived(logger, integrationEvent.Id, context.Source);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{{EventClass}} {Id} received from {Source}")]
    private static partial void LogReceived(ILogger logger, Guid id, string source);
}
// @stratum:end
// @stratum:end
