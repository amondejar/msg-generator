namespace Msg.Messaging.Kafka;

/// <summary>Configuration section "Messaging:Kafka".</summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Messaging:Kafka";

    /// <summary>Connection string Aspire injects for a Kafka resource called "kafka" (WithReference).</summary>
    public const string ConnectionStringName = "kafka";

    /// <summary>"host:port[,host:port]". Defaults to ConnectionStrings:kafka.</summary>
    public string? BootstrapServers { get; set; }

    /// <summary>Create missing topics (event topics and their dead-letter topics) on first use.</summary>
    public bool CreateTopics { get; set; } = true;

    /// <summary>
    /// Partitions of the topics MSG creates. 1 keeps every event of a topic in order; more partitions spread the
    /// load across the instances of a consumer, but events are only ordered within a partition.
    /// </summary>
    public int Partitions { get; set; } = 1;

    /// <summary>Copies of each partition. 1 for a local broker; 3 in a production cluster.</summary>
    public short ReplicationFactor { get; set; } = 1;

    /// <summary>Messages that fail every attempt go to "&lt;topic&gt;&lt;suffix&gt;".</summary>
    public string DeadLetterSuffix { get; set; } = ".dead-letter";
}
