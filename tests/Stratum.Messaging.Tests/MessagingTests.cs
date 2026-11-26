using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Stratum.Messaging.Tests;

public sealed class MessagingTests
{
    [Fact]
    public void TopicComesFromTheAttributeOrTheTypeName()
    {
        Assert.Equal("orders.order-created", EventTopology.TopicOf<OrderCreated>());
        Assert.Equal("customer-renamed", EventTopology.TopicOf<CustomerRenamed>());
    }

    [Fact]
    public async Task PublishedEventIsStoredInTheOutboxAndNotSentUntilDispatched()
    {
        using var host = TestHosts.Create();
        await host.PublishAsync(new OrderCreated(Guid.NewGuid(), 10m));

        var outbox = host.Services.GetRequiredService<InMemoryOutbox>();
        var transport = host.Services.GetRequiredService<InMemoryTransport>();
        Assert.Single(outbox.Messages);
        Assert.Empty(transport.Sent);

        var sent = await host.Services.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync();

        Assert.Equal(1, sent);
        var envelope = Assert.Single(transport.Sent);
        Assert.Equal("orders.order-created", envelope.Topic);
        Assert.Equal("orders", envelope.Source);
        Assert.NotNull(Assert.Single(outbox.Messages).SentAt);
    }

    [Fact]
    public async Task EventReachesTheHandlerEndToEnd()
    {
        using var host = TestHosts.Create(background: true);
        await host.StartAsync();
        var orderId = Guid.NewGuid();

        await host.PublishAsync(new OrderCreated(orderId, 42m));

        var probe = host.Services.GetRequiredService<Probe>();
        await probe.FirstHandled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var (received, context) = Assert.Single(probe.Received);
        Assert.Equal(orderId, received.OrderId);
        Assert.Equal(42m, received.Total);
        Assert.Equal("orders.order-created", context.Topic);
        Assert.Equal(1, context.Attempt);
        await host.StopAsync();
    }

    [Fact]
    public async Task ARedeliveredMessageIsHandledOnlyOnce()
    {
        using var host = TestHosts.Create();
        var consumer = host.Services.GetRequiredService<IntegrationEventConsumer>();
        var envelope = Envelope(new OrderCreated(Guid.NewGuid(), 1m));

        Assert.Equal(ConsumeResult.Completed, await consumer.HandleAsync(envelope, CancellationToken.None));
        Assert.Equal(ConsumeResult.Completed, await consumer.HandleAsync(envelope, CancellationToken.None));

        Assert.Single(host.Services.GetRequiredService<Probe>().Received);
    }

    [Fact]
    public async Task AFailingHandlerIsRetried()
    {
        using var host = TestHosts.Create();
        var probe = host.Services.GetRequiredService<Probe>();
        probe.FailFirst(2);

        var result = await host.Services.GetRequiredService<IntegrationEventConsumer>()
            .HandleAsync(Envelope(new OrderCreated(Guid.NewGuid(), 1m)), CancellationToken.None);

        Assert.Equal(ConsumeResult.Completed, result);
        Assert.Equal(3, probe.Calls);
        Assert.Equal(3, Assert.Single(probe.Received).Context.Attempt);
    }

    [Fact]
    public async Task AHandlerThatAlwaysFailsGoesToDeadLetterAfterMaxAttempts()
    {
        using var host = TestHosts.Create(m => m.Configure(o => o.MaxHandlerAttempts = 3));
        var probe = host.Services.GetRequiredService<Probe>();
        probe.FailFirst(int.MaxValue);

        var result = await host.Services.GetRequiredService<IntegrationEventConsumer>()
            .HandleAsync(Envelope(new OrderCreated(Guid.NewGuid(), 1m)), CancellationToken.None);

        Assert.Equal(ConsumeResult.DeadLetter, result);
        Assert.Equal(3, probe.Calls);
    }

    [Fact]
    public async Task AnUnreadableMessageGoesToDeadLetter()
    {
        using var host = TestHosts.Create();
        var envelope = Envelope(new OrderCreated(Guid.NewGuid(), 1m)) with { Payload = "{ not json" };

        var result = await host.Services.GetRequiredService<IntegrationEventConsumer>().HandleAsync(envelope, CancellationToken.None);

        Assert.Equal(ConsumeResult.DeadLetter, result);
    }

    [Fact]
    public async Task WhenTheBrokerIsDownTheEventStaysInTheOutboxAndIsSentLater()
    {
        using var host = TestHosts.Create(services: s =>
        {
            s.AddSingleton<FlakyTransport>();
            s.AddSingleton<IMessageTransport>(sp => sp.GetRequiredService<FlakyTransport>());
        });
        host.Services.GetRequiredService<FlakyTransport>().FailuresLeft = 1;
        await host.PublishAsync(new OrderCreated(Guid.NewGuid(), 1m));
        var dispatcher = host.Services.GetRequiredService<OutboxDispatcher>();
        var outbox = host.Services.GetRequiredService<InMemoryOutbox>();

        Assert.Equal(0, await dispatcher.DispatchPendingAsync());
        var pending = Assert.Single(outbox.Messages);
        Assert.Null(pending.SentAt);
        Assert.Equal(1, pending.Attempts);
        Assert.Equal("broker down", pending.LastError);

        pending.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);   // skip the wait
        Assert.Equal(1, await dispatcher.DispatchPendingAsync());
        Assert.Single(host.Services.GetRequiredService<InMemoryTransport>().Sent);
    }

    [Fact]
    public async Task TheConsumerTraceContinuesThePublisherTrace()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is MessagingTelemetry.SourceName or "test",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (activities) { activities.Add(a); } },
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("test");

        using var host = TestHosts.Create();
        ActivityTraceId traceId;
        using (var request = source.StartActivity("POST /orders"))
        {
            traceId = request!.TraceId;
            await host.PublishAsync(new OrderCreated(Guid.NewGuid(), 1m));
        }

        await host.Services.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync();
        var envelope = Assert.Single(host.Services.GetRequiredService<InMemoryTransport>().Sent);
        await host.Services.GetRequiredService<IntegrationEventConsumer>().HandleAsync(envelope, CancellationToken.None);

        lock (activities)
        {
            // Other tests run in parallel and also produce activities: look only at this trace.
            Assert.Single(activities, a => a.Kind == ActivityKind.Consumer && a.TraceId == traceId);
            Assert.Single(activities, a => a.Kind == ActivityKind.Producer && a.TraceId == traceId);
        }
    }

    [Fact]
    public async Task WithoutABrokerTheErrorSaysWhatToConfigure()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddStratumMessaging("orders").Configure(o => o.OutboxDispatcherEnabled = false);
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Services.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync());

        Assert.Contains("UseInMemory", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheJournalRecordsWhatHappenedInEachHandler()
    {
        using var host = TestHosts.Create(m => m.Configure(o => o.MaxHandlerAttempts = 3));
        var probe = host.Services.GetRequiredService<Probe>();
        var consumer = host.Services.GetRequiredService<IntegrationEventConsumer>();
        var journal = host.Services.GetRequiredService<InMemoryJournal>();

        probe.FailFirst(1);
        var retried = Envelope(new OrderCreated(Guid.NewGuid(), 1m));
        await consumer.HandleAsync(retried, CancellationToken.None);

        probe.FailFirst(int.MaxValue);
        var failed = Envelope(new OrderCreated(Guid.NewGuid(), 2m));
        await consumer.HandleAsync(failed, CancellationToken.None);

        var unreadable = Envelope(new OrderCreated(Guid.NewGuid(), 3m)) with { Payload = "{ not json" };
        await consumer.HandleAsync(unreadable, CancellationToken.None);

        var rows = journal.Received.ToDictionary(r => r.MessageId);
        Assert.Equal(ReceivedStatus.Processed, rows[retried.MessageId].Status);
        Assert.Equal(2, rows[retried.MessageId].Attempts);
        Assert.Null(rows[retried.MessageId].LastError);
        Assert.Equal(ReceivedStatus.DeadLettered, rows[failed.MessageId].Status);
        Assert.Equal(3, rows[failed.MessageId].Attempts);
        Assert.Contains("simulated failure", rows[failed.MessageId].LastError, StringComparison.Ordinal);
        Assert.Equal(ReceivedStatus.Unreadable, rows[unreadable.MessageId].Status);
    }

    [Fact]
    public async Task TheDiagnosticsShowWhatWasSentAndReceived()
    {
        using var host = TestHosts.Create();
        await host.PublishAsync(new OrderCreated(Guid.NewGuid(), 10m));
        await host.Services.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync();
        var sent = Assert.Single(host.Services.GetRequiredService<InMemoryTransport>().Sent);
        await host.Services.GetRequiredService<IntegrationEventConsumer>().HandleAsync(sent, CancellationToken.None);

        var snapshot = await host.Services.GetRequiredService<MessagingDiagnostics>().SnapshotAsync(TimeSpan.FromMinutes(5), 50, CancellationToken.None);

        Assert.Equal("orders", snapshot.Service);
        Assert.Equal("inMemory", snapshot.Broker);
        var outbox = Assert.Single(snapshot.Sent);
        Assert.Equal(SentStatus.Sent, outbox.Status);
        Assert.Equal(sent.MessageId, outbox.MessageId);
        Assert.Equal(sent.MessageId, Assert.Single(snapshot.Received).MessageId);
        Assert.Contains(snapshot.Handles, h => h.Topic == "orders.order-created");
    }

    [Fact]
    public async Task AFailedMessageCanBeSentAgainAndOnlyTheFailedHandlersRunIt()
    {
        using var host = TestHosts.Create();
        var probe = host.Services.GetRequiredService<Probe>();
        probe.FailFirst(5);
        await host.PublishAsync(new OrderCreated(Guid.NewGuid(), 10m));
        var dispatcher = host.Services.GetRequiredService<OutboxDispatcher>();
        var transport = host.Services.GetRequiredService<InMemoryTransport>();
        var consumer = host.Services.GetRequiredService<IntegrationEventConsumer>();
        var diagnostics = host.Services.GetRequiredService<MessagingDiagnostics>();
        await dispatcher.DispatchPendingAsync();
        var first = Assert.Single(transport.Sent);
        Assert.Equal(ConsumeResult.DeadLetter, await consumer.HandleAsync(first, CancellationToken.None));

        var outcome = await diagnostics.ResendAsync(Guid.Parse(first.MessageId), CancellationToken.None);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(SentStatus.Pending, Assert.Single((await diagnostics.SnapshotAsync(TimeSpan.FromMinutes(5), 50, CancellationToken.None)).Sent).Status);
        await dispatcher.DispatchPendingAsync();
        var again = transport.Sent.Last();
        Assert.Equal(first.MessageId, again.MessageId);
        Assert.Equal(ConsumeResult.Completed, await consumer.HandleAsync(again, CancellationToken.None));
        Assert.Single(probe.Received);
        var row = Assert.Single((await diagnostics.SnapshotAsync(TimeSpan.FromMinutes(5), 50, CancellationToken.None)).Received);
        Assert.Equal(ReceivedStatus.Processed, row.Status);

        // Once processed, sending it again runs nothing (inbox).
        await diagnostics.ResendAsync(Guid.Parse(first.MessageId), CancellationToken.None);
        await dispatcher.DispatchPendingAsync();
        await consumer.HandleAsync(transport.Sent.Last(), CancellationToken.None);
        Assert.Single(probe.Received);
        Assert.False((await diagnostics.ResendAsync(Guid.NewGuid(), CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task ATestEventGoesThroughTheOutboxLikeARealOne()
    {
        using var host = TestHosts.Create();
        var diagnostics = host.Services.GetRequiredService<MessagingDiagnostics>();
        var orderId = Guid.NewGuid();

        var outcome = await diagnostics.PublishTestAsync("orders.order-created", new System.Text.Json.Nodes.JsonObject { ["orderId"] = orderId, ["total"] = 42.5m }, CancellationToken.None);

        Assert.True(outcome.Ok, outcome.Message);
        await host.Services.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync();
        var sent = Assert.Single(host.Services.GetRequiredService<InMemoryTransport>().Sent);
        Assert.Equal(outcome.MessageId, sent.MessageId);
        var received = System.Text.Json.JsonSerializer.Deserialize<OrderCreated>(sent.Payload, EventTopology.JsonOptions)!;
        Assert.Equal(orderId, received.OrderId);
        Assert.Equal(42.5m, received.Total);
        Assert.Equal(sent.MessageId, received.EventId.ToString());

        Assert.False((await diagnostics.PublishTestAsync("nobody.nothing", null, CancellationToken.None)).Ok);
        Assert.False((await diagnostics.PublishTestAsync("orders.order-created", new System.Text.Json.Nodes.JsonObject { ["total"] = "a lot" }, CancellationToken.None)).Ok);
    }

    [Fact]
    public void TheTraceIdComesFromTheTraceparent()
    {
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", MessagingTelemetry.TraceIdOf("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"));
        Assert.Null(MessagingTelemetry.TraceIdOf(null));
        Assert.Null(MessagingTelemetry.TraceIdOf("nonsense"));
    }

    private static MessageEnvelope Envelope(OrderCreated e) => new()
    {
        MessageId = e.EventId.ToString(),
        Topic = EventTopology.TopicOf<OrderCreated>(),
        Source = "orders",
        Payload = System.Text.Json.JsonSerializer.Serialize(e, EventTopology.JsonOptions),
        OccurredAt = e.OccurredAt,
    };
}
