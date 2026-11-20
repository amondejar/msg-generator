using Microsoft.EntityFrameworkCore;

namespace Msg.Messaging.EntityFrameworkCore;

public static class MessagingModelBuilderExtensions
{
    /// <summary>
    /// Tables msg_outbox and msg_inbox. Call it from OnModelCreating; "dotnet ef migrations add" then creates them.
    /// </summary>
    public static ModelBuilder AddMsgMessaging(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("msg_outbox");
            b.HasKey(m => m.Id);
            b.Property(m => m.Topic).HasMaxLength(250).IsRequired();
            b.Property(m => m.Type).HasMaxLength(500).IsRequired();
            b.Property(m => m.Payload).IsRequired();
            b.Property(m => m.CorrelationId).HasMaxLength(100);
            b.Property(m => m.TraceParent).HasMaxLength(100);
            b.Property(m => m.LastError).HasMaxLength(2000);
            b.HasIndex(m => new { m.SentAt, m.NextAttemptAt, m.OccurredAt });
        });

        modelBuilder.Entity<InboxMessage>(b =>
        {
            b.ToTable("msg_inbox");
            b.HasKey(m => new { m.MessageId, m.Consumer });
            b.Property(m => m.MessageId).HasMaxLength(100);
            b.Property(m => m.Consumer).HasMaxLength(500);
        });

        return modelBuilder;
    }
}
