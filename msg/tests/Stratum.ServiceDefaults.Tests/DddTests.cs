using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Stratum.Ddd;

namespace Stratum.ServiceDefaults.Tests;

public sealed class DddTests
{
    [Fact]
    public async Task DomainExceptionBecomes422WithMessage()
    {
        await using var app = await TestApp.StartAsync(configureApp: app =>
            app.MapPost("/orders", string () => throw new DomainException("Quantity must be positive.")));
        using var client = app.GetTestClient();

        var response = await client.PostAsync(new Uri("/orders", UriKind.Relative), null);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Quantity must be positive.", body.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task HandlersAreDiscoveredAndEventsReachEveryHandler()
    {
        await using var app = await TestApp.StartAsync(builder =>
        {
            builder.Services.AddSingleton<Calls>();
            builder.Services.AddStratumApplication(typeof(DddTests).Assembly);
        });

        using var scope = app.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PlaceOrder, Guid>>();
        var id = await handler.HandleAsync(new PlaceOrder(3), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(new[] { "email:3", "stock:3" }, scope.ServiceProvider.GetRequiredService<Calls>().Lines);
    }

    [Fact]
    public void AggregateRaisesAndClearsEvents()
    {
        var order = Order.Place(2);

        var placed = Assert.IsType<OrderPlaced>(Assert.Single(order.DomainEvents));
        Assert.Equal(2, placed.Quantity);

        order.ClearDomainEvents();
        Assert.Empty(order.DomainEvents);
        Assert.Throws<DomainException>(() => Order.Place(0));
    }

    internal sealed class Calls
    {
        public List<string> Lines { get; } = [];
    }

    internal sealed record OrderPlaced(Guid OrderId, int Quantity) : DomainEvent;

    internal sealed class Order : AggregateRoot<Guid>
    {
        public static Order Place(int quantity)
        {
            if (quantity <= 0)
            {
                throw new DomainException("Quantity must be positive.");
            }

            var order = new Order { Id = Guid.CreateVersion7() };
            order.Raise(new OrderPlaced(order.Id, quantity));
            return order;
        }
    }

    internal sealed record PlaceOrder(int Quantity) : ICommand<Guid>;

    internal sealed class PlaceOrderHandler(IDomainEventPublisher publisher) : ICommandHandler<PlaceOrder, Guid>
    {
        public async Task<Guid> HandleAsync(PlaceOrder command, CancellationToken cancellationToken)
        {
            var order = Order.Place(command.Quantity);
            foreach (var domainEvent in order.DomainEvents)
            {
                await publisher.PublishAsync(domainEvent, cancellationToken);
            }

            return order.Id;
        }
    }

    internal sealed class SendEmail(Calls calls) : IDomainEventHandler<OrderPlaced>
    {
        public Task HandleAsync(OrderPlaced domainEvent, CancellationToken cancellationToken)
        {
            calls.Lines.Add($"email:{domainEvent.Quantity}");
            return Task.CompletedTask;
        }
    }

    internal sealed class ReserveStock(Calls calls) : IDomainEventHandler<OrderPlaced>
    {
        public Task HandleAsync(OrderPlaced domainEvent, CancellationToken cancellationToken)
        {
            calls.Lines.Add($"stock:{domainEvent.Quantity}");
            return Task.CompletedTask;
        }
    }
}
