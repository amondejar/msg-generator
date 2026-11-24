using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stratum.Messaging.EntityFrameworkCore;

/// <summary>Tables msg_outbox, msg_inbox and msg_received. "dotnet ef migrations add" creates them.</summary>
public static class MessagingModel
{
    /// <summary>The three tables, from OnModelCreating. (Stratum generates IEntityTypeConfiguration classes instead.)</summary>
    public static ModelBuilder AddStratumMessaging(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<OutboxMessage>(ConfigureOutbox);
        modelBuilder.Entity<InboxMessage>(ConfigureInbox);
        modelBuilder.Entity<ReceivedMessage>(ConfigureReceived);
        return modelBuilder;
    }

    public static void ConfigureOutbox(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("msg_outbox");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Topic).HasMaxLength(250).IsRequired();
        builder.Property(m => m.Type).HasMaxLength(500).IsRequired();
        builder.Property(m => m.Payload).IsRequired();
        builder.Property(m => m.CorrelationId).HasMaxLength(100);
        builder.Property(m => m.TraceParent).HasMaxLength(100);
        builder.Property(m => m.LastError).HasMaxLength(2000);
        builder.HasIndex(m => new { m.SentAt, m.NextAttemptAt, m.OccurredAt });
    }

    public static void ConfigureInbox(EntityTypeBuilder<InboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("msg_inbox");
        builder.HasKey(m => new { m.MessageId, m.Consumer });
        builder.Property(m => m.MessageId).HasMaxLength(100);
        builder.Property(m => m.Consumer).HasMaxLength(500);
    }

    /// <summary>Journal of received messages, failures included (Stratum Studio → Event traces).</summary>
    public static void ConfigureReceived(EntityTypeBuilder<ReceivedMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("msg_received");
        builder.HasKey(m => new { m.MessageId, m.Consumer });
        builder.Property(m => m.MessageId).HasMaxLength(100);
        builder.Property(m => m.Consumer).HasMaxLength(500);
        builder.Property(m => m.Topic).HasMaxLength(250).IsRequired();
        builder.Property(m => m.Source).HasMaxLength(250).IsRequired();
        builder.Property(m => m.CorrelationId).HasMaxLength(100);
        builder.Property(m => m.TraceId).HasMaxLength(32);
        builder.Property(m => m.Status).HasMaxLength(20).IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(2000);
        builder.HasIndex(m => m.UpdatedAt);
    }
}
