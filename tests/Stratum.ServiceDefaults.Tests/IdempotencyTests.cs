using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace Stratum.ServiceDefaults.Tests;

public sealed class IdempotencyTests
{
    private int _created;

    private Task<WebApplication> StartAsync() => TestApp.StartAsync(configureApp: app =>
        app.MapPost("/items", (Item item) =>
        {
            if (string.IsNullOrEmpty(item.Name))
            {
                return Results.Problem(statusCode: 400, title: "Name is required");
            }

            var id = Interlocked.Increment(ref _created);
            return Results.Created($"/items/{id}", new { id, item.Name });
        }));

    [Fact]
    public async Task ARetryWithTheSameKeyGetsTheSameResponseWithoutRunningAgain()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var first = await PostAsync(client, "key-1", new Item("pen"));
        using var second = await PostAsync(client, "key-1", new Item("pen"));

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(1, _created);
    }

    [Fact]
    public async Task TheSameKeyWithAnotherRequestIs422()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var first = await PostAsync(client, "key-2", new Item("pen"));
        using var other = await PostAsync(client, "key-2", new Item("book"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
        Assert.Equal(1, _created);
    }

    [Fact]
    public async Task WithoutKeyEveryRequestRuns()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var first = await PostAsync(client, null, new Item("pen"));
        using var second = await PostAsync(client, null, new Item("pen"));

        Assert.Equal(2, _created);
        Assert.False(second.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task AnErrorIsNotRememberedSoTheFixedRetryRuns()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var invalid = await PostAsync(client, "key-3", new Item(""));
        using var fixedRetry = await PostAsync(client, "key-3", new Item("pen"));

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Created, fixedRetry.StatusCode);
        Assert.Equal(1, _created);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string? key, Item item)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/items", UriKind.Relative)) { Content = JsonContent.Create(item) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return client.SendAsync(request);
    }

    public sealed record Item(string Name);
}
