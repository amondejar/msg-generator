using System.Net;
using System.Net.Http.Json;
using {{Service}}.Api.Features;
using {{Service}}.Api.Features.{{Entities}};

namespace {{Service}}.Api.Tests.Features;

/// <summary>Created by "msg add entity". This file is yours: extend it with your business rules.</summary>
public sealed class {{Entity}}EndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Uri Collection = new("/api/v1/{{entities-kebab}}", UriKind.Relative);

    [Fact]
    public async Task CreateGetUpdateListDelete()
    {
        using var client = {{TestClient}};

        // Create
        var create = await client.PostAsJsonAsync(Collection, SampleRequest());
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<{{Entity}}Response>();
        Assert.NotNull(created);
        Assert.NotNull(create.Headers.Location);
        var item = new Uri($"/api/v1/{{entities-kebab}}/{created.Id}", UriKind.Relative);

        // Get
        var fetched = await client.GetFromJsonAsync<{{Entity}}Response>(item);
        Assert.Equal(created.Id, fetched?.Id);

        // Update
        var update = await client.PutAsJsonAsync(item, SampleRequest());
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        // List
        var page = await client.GetFromJsonAsync<PagedResponse<{{Entity}}Response>>(Collection);
        Assert.NotNull(page);
        Assert.Contains(page.Items, e => e.Id == created.Id);

        // Delete
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(item)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(item)).StatusCode);
    }

    [Fact]
    public async Task UnknownIdReturnsNotFound()
    {
        using var client = {{TestClient}};
        var item = new Uri($"/api/v1/{{entities-kebab}}/{Guid.NewGuid()}", UriKind.Relative);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(item)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(item, SampleRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(item)).StatusCode);
    }
    // @msg:if hasRequiredString

    [Fact]
    public async Task InvalidRequestReturnsValidationProblem()
    {
        using var client = {{TestClient}};

        var response = await client.PostAsJsonAsync(Collection, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
    // @msg:end
    // @msg:if auth

    [Fact]
    public async Task AnonymousCallerIsRejected()
    {
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Collection)).StatusCode);
    }
    // @msg:end

    private static {{Entity}}Request SampleRequest() => new(
{{SampleArguments}});
}
