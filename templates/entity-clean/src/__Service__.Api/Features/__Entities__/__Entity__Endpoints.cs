using {{Service}}.Application;
using {{Service}}.Application.{{Entities}};
using Microsoft.AspNetCore.Http.HttpResults;
using Msg.Ddd;

namespace {{Service}}.Api.Features.{{Entities}};

/// <summary>
/// Thin endpoints for /api/v1/{{entities-kebab}}: translate HTTP to use cases (Application) and back.
/// No business logic here.
/// </summary>
public static class {{Entity}}Endpoints
{
    public static RouteGroupBuilder Map{{Entity}}Endpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var group = api.MapGroup("/{{entities-kebab}}").WithTags("{{Entities}}");

        group.MapGet(string.Empty, List).WithName("List{{Entities}}");
        group.MapGet("/{id:guid}", Get).WithName("Get{{Entity}}");
        group.MapPost(string.Empty, Create).WithName("Create{{Entity}}");
        group.MapPut("/{id:guid}", Update).WithName("Update{{Entity}}");
        group.MapDelete("/{id:guid}", Delete).WithName("Delete{{Entity}}");

        return api;
    }

    private static async Task<Ok<PagedResponse<{{Entity}}Response>>> List(
        IQueryHandler<List{{Entities}}, PagedResult<{{Entity}}Dto>> handler,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var (p, size) = PagedResponse<{{Entity}}Response>.Normalize(page, pageSize);
        var result = await handler.HandleAsync(new List{{Entities}}(p, size), cancellationToken);

        return TypedResults.Ok(new PagedResponse<{{Entity}}Response>(
            [.. result.Items.Select({{Entity}}Response.From)], result.Page, result.PageSize, result.Total));
    }

    private static async Task<Results<Ok<{{Entity}}Response>, NotFound>> Get(
        Guid id, IQueryHandler<Get{{Entity}}, {{Entity}}Dto?> handler, CancellationToken cancellationToken)
    {
        var dto = await handler.HandleAsync(new Get{{Entity}}(id), cancellationToken);
        return dto is null ? TypedResults.NotFound() : TypedResults.Ok({{Entity}}Response.From(dto));
    }

    private static async Task<Results<CreatedAtRoute<{{Entity}}Response>, NotFound>> Create(
        {{Entity}}Request request,
        ICommandHandler<Create{{Entity}}, Guid> handler,
        IQueryHandler<Get{{Entity}}, {{Entity}}Dto?> query,
        CancellationToken cancellationToken)
    {
        var id = await handler.HandleAsync(new Create{{Entity}}({{RequestToCommand}}), cancellationToken);
        var dto = await query.HandleAsync(new Get{{Entity}}(id), cancellationToken);

        return dto is null
            ? TypedResults.NotFound()
            : TypedResults.CreatedAtRoute({{Entity}}Response.From(dto), "Get{{Entity}}", new { id });
    }

    private static async Task<Results<NoContent, NotFound>> Update(
        Guid id, {{Entity}}Request request, ICommandHandler<Update{{Entity}}, bool> handler, CancellationToken cancellationToken)
    {
        var updated = await handler.HandleAsync(new Update{{Entity}}(id, {{RequestToCommand}}), cancellationToken);
        return updated ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, NotFound>> Delete(
        Guid id, ICommandHandler<Delete{{Entity}}, bool> handler, CancellationToken cancellationToken)
    {
        var deleted = await handler.HandleAsync(new Delete{{Entity}}(id), cancellationToken);
        return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
