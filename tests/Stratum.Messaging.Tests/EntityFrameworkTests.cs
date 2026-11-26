using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratum.Ddd;
using Stratum.Messaging.EntityFrameworkCore;

namespace Stratum.Messaging.Tests;

public sealed class Order : AggregateRoot<Guid>
{
    public decimal Total { get; private set; }

    public static Order Create(decimal total)
    {
        var order = new Order { Id = Guid.CreateVersion7(), Total = total };
        order.Raise(new OrderPlaced(order.Id, total));
        return order;
    }
}

public sealed record OrderPlaced(Guid OrderId, decimal Total) : DomainEvent;

/// <summary>What Stratum generates from stratum.json for the events marked as published.</summary>
public sealed class OrderEventsMapper : IIntegrationEventMapper
{
    public IEnumerable<IIntegrationEvent> Map(object domainEvent, object source) => domainEvent switch
    {
        OrderPlaced e => [new OrderCreated(e.OrderId, e.Total)],
        _ => [],
    };
}

public sealed class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.HasKey(o => o.Id);
            b.Ignore(o => o.DomainEvents);
        });
        modelBuilder.AddStratumMessaging();
    }
}

/// <summary>A handler that writes to the database, to check the inbox transaction.</summary>
public sealed class SaveOrderHandler(ShopDbContext db, Probe probe) : IIntegrationEventHandler<OrderCreated>
{
    public async Task HandleAsync(OrderCreated integrationEvent, MessageContext context, CancellationToken cancellationToken)
    {
        db.Orders.Add(Order.Create(integrationEvent.Total));
        await db.SaveChangesAsync(cancellationToken);
        probe.Record(integrationEvent, context);   // may throw after saving: the transaction must undo the save
    }
}

public sealed class EntityFrameworkTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<Probe>();
        builder.Services.AddDbContext<ShopDbContext>((sp, options) => options
            .UseSqlite(_connection)
            .AddInterceptors(sp.GetRequiredService<IntegrationEventsInterceptor>()));
        builder.AddStratumMessaging("shop")
            .UseInMemory()
            .UseEntityFramework<ShopDbContext>()
            .AddHandler<OrderCreated, SaveOrderHandler>()
            .Configure(o =>
            {
                o.ConsumersEnabled = false;
                o.OutboxDispatcherEnabled = false;
                o.RetryDelay = TimeSpan.FromMilliseconds(1);
                o.MaxHandlerAttempts = 2;
            });
        builder.Services.AddSingleton<IIntegrationEventMapper, OrderEventsMapper>();
        _host = builder.Build();

        await using var scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        _host.Dispose();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task PublishedEventsAreSavedWithTheChangesAndOnlyThen()
    {
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(new CustomerRenamed(Guid.NewGuid(), "Ana"));
            // No SaveChanges: like a request that failed before saving.
        }

        Assert.Equal(0, await CountAsync<OutboxMessage>());

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(new CustomerRenamed(Guid.NewGuid(), "Ana"));
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await CountAsync<OutboxMessage>());
    }

    [Fact]
    public async Task DomainEventsMarkedAsPublishedGoToTheOutboxInTheSameSave()
    {
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            db.Orders.Add(Order.Create(99m));
            await db.SaveChangesAsync();
        }

        await using var check = _host.Services.CreateAsyncScope();
        var message = await check.ServiceProvider.GetRequiredService<ShopDbContext>().Set<OutboxMessage>().SingleAsync();
        Assert.Equal("orders.order-created", message.Topic);
        Assert.Contains("99", message.Payload, StringComparison.Ordinal);

        var sent = await _host.Services.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync();
        Assert.Equal(1, sent);
        Assert.Equal(0, await CountAsync<OutboxMessage>(m => m.SentAt == null));
    }

    [Fact]
    public async Task TheInboxStopsDuplicatesAcrossScopes()
    {
        var consumer = _host.Services.GetRequiredService<IntegrationEventConsumer>();
        var envelope = Envelope(new OrderCreated(Guid.NewGuid(), 5m));

        await consumer.HandleAsync(envelope, CancellationToken.None);
        await consumer.HandleAsync(envelope, CancellationToken.None);

        Assert.Equal(1, await CountAsync<Order>());
        Assert.Equal(1, await CountAsync<InboxMessage>());
    }

    [Fact]
    public async Task AFailedHandlerLeavesNoChangesBehind()
    {
        _host.Services.GetRequiredService<Probe>().FailFirst(int.MaxValue);

        var result = await _host.Services.GetRequiredService<IntegrationEventConsumer>()
            .HandleAsync(Envelope(new OrderCreated(Guid.NewGuid(), 5m)), CancellationToken.None);

        Assert.Equal(ConsumeResult.DeadLetter, result);
        Assert.Equal(0, await CountAsync<Order>());
        Assert.Equal(0, await CountAsync<InboxMessage>());
    }

    [Fact]
    public async Task TheJournalKeepsFailuresAndSuccessesInMsgReceived()
    {
        var probe = _host.Services.GetRequiredService<Probe>();
        probe.FailFirst(1);
        var consumer = _host.Services.GetRequiredService<IntegrationEventConsumer>();
        var envelope = Envelope(new OrderCreated(Guid.NewGuid(), 5m));

        Assert.Equal(ConsumeResult.Completed, await consumer.HandleAsync(envelope, CancellationToken.None));

        // The first attempt failed inside the handler's transaction (rolled back); the journal is written apart.
        await using var scope = _host.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Set<ReceivedMessage>().SingleAsync();
        Assert.Equal(ReceivedStatus.Processed, row.Status);
        Assert.Equal(2, row.Attempts);
        Assert.Equal("orders", row.Source);

        var snapshot = await _host.Services.GetRequiredService<MessagingDiagnostics>().SnapshotAsync(TimeSpan.FromHours(1), 50, CancellationToken.None);
        Assert.Equal(envelope.MessageId, Assert.Single(snapshot.Received).MessageId);
        Assert.Empty(snapshot.Problems);
    }

    [Fact]
    public async Task CleanupDeletesOldSentMessagesButKeepsPendingOnes()
    {
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            db.Orders.Add(Order.Create(1m));
            await db.SaveChangesAsync();
        }

        await _host.Services.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync();
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            db.Orders.Add(Order.Create(2m));
            await db.SaveChangesAsync();   // pending: never deleted
        }

        _host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MessagingOptions>>().Value.TraceRetention = TimeSpan.Zero;
        var removed = await _host.Services.GetRequiredService<OutboxDispatcher>().CleanupAsync();

        Assert.Equal(1, removed);
        Assert.Equal(1, await CountAsync<OutboxMessage>(m => m.SentAt == null));
        Assert.Equal(0, await CountAsync<OutboxMessage>(m => m.SentAt != null));
    }

    [Fact]
    public async Task TestEventsAndResendsUseTheOutboxTable()
    {
        var diagnostics = _host.Services.GetRequiredService<MessagingDiagnostics>();
        var outcome = await diagnostics.PublishTestAsync(
            EventTopology.TopicOf<CustomerRenamed>(), new System.Text.Json.Nodes.JsonObject { ["customerId"] = Guid.NewGuid(), ["name"] = "Ana" }, CancellationToken.None);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(1, await CountAsync<OutboxMessage>(m => m.SentAt == null));

        await _host.Services.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync();
        Assert.Equal(0, await CountAsync<OutboxMessage>(m => m.SentAt == null));

        Assert.True((await diagnostics.ResendAsync(Guid.Parse(outcome.MessageId!), CancellationToken.None)).Ok);
        Assert.Equal(1, await CountAsync<OutboxMessage>(m => m.SentAt == null && m.Attempts == 0));
        Assert.False((await diagnostics.ResendAsync(Guid.NewGuid(), CancellationToken.None)).Ok);
    }

    private async Task<int> CountAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>>? predicate = null)
        where T : class
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var set = scope.ServiceProvider.GetRequiredService<ShopDbContext>().Set<T>();
        return predicate is null ? await set.CountAsync() : await set.CountAsync(predicate);
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
