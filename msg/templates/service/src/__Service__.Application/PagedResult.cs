namespace {{Service}}.Application;

/// <summary>A page of results returned by list queries.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
