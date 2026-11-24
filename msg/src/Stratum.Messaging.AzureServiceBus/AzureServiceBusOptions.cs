namespace Stratum.Messaging.AzureServiceBus;

/// <summary>Configuration section "Messaging:AzureServiceBus".</summary>
public sealed class AzureServiceBusOptions
{
    public const string SectionName = "Messaging:AzureServiceBus";

    /// <summary>Connection string Aspire injects for a Service Bus resource called "servicebus" (WithReference).</summary>
    public const string ConnectionStringName = "servicebus";

    /// <summary>Defaults to ConnectionStrings:servicebus (the local emulator's, with Aspire).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Create missing topics and subscriptions on first use (needs "Manage" rights). Off by default: Aspire declares
    /// them for the emulator and provisions them in Azure. Ignored with the emulator, which doesn't support it.
    /// </summary>
    public bool CreateEntities { get; set; }

    /// <summary>Messages handled at the same time per topic. 1 keeps them in order.</summary>
    public int MaxConcurrentCalls { get; set; } = 1;
}
