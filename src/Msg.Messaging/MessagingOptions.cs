using System.ComponentModel.DataAnnotations;

namespace Msg.Messaging;

/// <summary>Configuration section "Messaging".</summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>This service's name: source of the events it publishes and its subscription / consumer group.</summary>
    [Required]
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>How often the outbox is checked for pending events.</summary>
    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    [Range(1, 1000)]
    public int OutboxBatchSize { get; set; } = 50;

    /// <summary>Attempts to send an outbox event before it's left for a person to look at (LastError).</summary>
    [Range(1, 100)]
    public int OutboxMaxAttempts { get; set; } = 10;

    /// <summary>Attempts to handle a received event before it goes to the dead-letter queue.</summary>
    [Range(1, 20)]
    public int MaxHandlerAttempts { get; set; } = 5;

    /// <summary>Wait before the second attempt; doubled for each one after that.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Turn off to only publish (e.g. a service with no subscriptions, or in tests).</summary>
    public bool ConsumersEnabled { get; set; } = true;

    public bool OutboxDispatcherEnabled { get; set; } = true;

    internal TimeSpan BackoffFor(int attempt) => RetryDelay * Math.Pow(2, Math.Max(0, attempt - 1));
}
