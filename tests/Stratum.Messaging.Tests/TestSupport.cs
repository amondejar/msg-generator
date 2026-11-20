using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stratum.Messaging.Tests;

[IntegrationEvent("orders.order-created")]
public sealed record OrderCreated(Guid OrderId, decimal Total) : IntegrationEvent;

public sealed record CustomerRenamed(Guid CustomerId, string Name) : IntegrationEvent;

/// <summary>Records what handlers received; optionally fails the first N calls.</summary>
public sealed class Probe
{
    private int _failuresLeft;
    private int _calls;

    public ConcurrentQueue<(OrderCreated Event, MessageContext Context)> Received { get; } = new();

    public int Calls => Volatile.Read(ref _calls);

    public TaskCompletionSource FirstHandled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void FailFirst(int times) => _failuresLeft = times;

    public void Record(OrderCreated e, MessageContext context)
    {
        Interlocked.Increment(ref _calls);
        if (Interlocked.Decrement(ref _failuresLeft) >= 0)
        {
            throw new InvalidOperationException("simulated failure");
        }

        Received.Enqueue((e, context));
        FirstHandled.TrySetResult();
    }
}

public sealed class OrderCreatedHandler(Probe probe) : IIntegrationEventHandler<OrderCreated>
{
    public Task HandleAsync(OrderCreated integrationEvent, MessageContext context, CancellationToken cancellationToken)
    {
        probe.Record(integrationEvent, context);
        return Task.CompletedTask;
    }
}

/// <summary>Transport that fails the first N sends.</summary>
public sealed class FlakyTransport(InMemoryTransport inner) : IMessageTransport
{
    public int FailuresLeft { get; set; }

    public Task SendAsync(MessageEnvelope envelope, CancellationToken cancellationToken) =>
        FailuresLeft-- > 0 ? throw new InvalidOperationException("broker down") : inner.SendAsync(envelope, cancellationToken);

    public Task ConsumeAsync(IReadOnlyCollection<string> topics, string subscription, Func<MessageEnvelope, CancellationToken, Task<ConsumeResult>> onMessage, CancellationToken cancellationToken) =>
        inner.ConsumeAsync(topics, subscription, onMessage, cancellationToken);
}

internal static class TestHosts
{
    /// <summary>A service with in-memory messaging and the OrderCreated handler; background loops off unless asked.</summary>
    public static IHost Create(Action<MessagingBuilder>? configure = null, bool background = false, Action<IServiceCollection>? services = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<Probe>();
        var messaging = builder.AddStratumMessaging("orders")
            .UseInMemory()
            .AddHandler<OrderCreated, OrderCreatedHandler>()
            .Configure(o =>
            {
                o.RetryDelay = TimeSpan.FromMilliseconds(1);
                o.OutboxPollInterval = TimeSpan.FromMilliseconds(20);
                o.ConsumersEnabled = background;
                o.OutboxDispatcherEnabled = background;
            });
        configure?.Invoke(messaging);
        services?.Invoke(builder.Services);
        return builder.Build();
    }

    public static async Task PublishAsync<TEvent>(this IHost host, TEvent integrationEvent)
        where TEvent : class, IIntegrationEvent
    {
        await using var scope = host.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        await bus.PublishAsync(integrationEvent);
    }
}
