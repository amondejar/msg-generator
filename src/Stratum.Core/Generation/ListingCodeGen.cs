using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>
/// Sort and search of the generated lists: GET /api/v1/customers?sort=-name&amp;search=ana. Every plain field (and
/// createdAt/updatedAt) can sort, with "-" for descending; the search looks for the text in the text fields, ignoring case.
/// </summary>
public static class ListingCodeGen
{
    private static readonly HashSet<string> SortableTypes = new(StringComparer.Ordinal) { "string", "int", "long", "decimal", "bool", "guid", "date", "datetime", PropertyTypes.EnumType };

    /// <summary>Fields of the entity that sort the list (createdAt and updatedAt aside).</summary>
    public static IReadOnlyList<PropertyDefinition> Sortable(EntityDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return [.. entity.Properties.Where(p => !p.IsList && p.References is null && SortableTypes.Contains(p.Type))];
    }

    /// <summary>Text fields the search looks into.</summary>
    public static IReadOnlyList<PropertyDefinition> Searchable(EntityDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return [.. entity.Properties.Where(ValueRules.IsText)];
    }

    /// <summary>The names accepted by ?sort= (camelCase, as in the JSON).</summary>
    public static IReadOnlyList<string> SortNames(EntityDefinition entity) =>
        [.. Sortable(entity).Select(p => EntityCodeGen.Camel(p.Name)), "createdAt", "updatedAt"];

    public static Dictionary<string, string> Tokens(EntityDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var names = Sortable(entity).Select(p => (Json: EntityCodeGen.Camel(p.Name), Member: p.Name))
            .Concat([(Json: "createdAt", Member: "CreatedAt"), (Json: "updatedAt", Member: "UpdatedAt")]).ToList();
        var searchable = Searchable(entity);
        var firstText = searchable.FirstOrDefault();
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SortFields"] = string.Join(", ", names.Select(n => $"\"{n.Json}\"")),
            ["SortFieldsText"] = string.Join(", ", names.Select(n => n.Json)),
            // Case labels in lower case: ?sort=Name and ?sort=name are the same. Ties keep the creation order.
            ["SortCases"] = string.Join("\n", names.SelectMany(n => new[]
            {
                $"            \"{n.Json.ToLowerInvariant()}\" => rows.OrderBy(e => e.{n.Member}).ThenBy(e => e.Id),",
                $"            \"-{n.Json.ToLowerInvariant()}\" => rows.OrderByDescending(e => e.{n.Member}).ThenBy(e => e.Id),",
            })),
            ["SearchFieldsText"] = searchable.Count == 0 ? "none" : string.Join(", ", searchable.Select(p => EntityCodeGen.Camel(p.Name))),
            ["SearchCondition"] = searchable.Count == 0 ? "false" : string.Join("\n                || ", searchable.Select(p => p.Nullable
                ? $"(e.{p.Name} != null && e.{p.Name}.ToLower().Contains(term))"
                : $"e.{p.Name}.ToLower().Contains(term)")),
            ["SearchTestField"] = firstText?.Name ?? "Id",
            ["SearchTestSortName"] = firstText is null ? "createdAt" : EntityCodeGen.Camel(firstText.Name),
            ["SearchTestTerm"] = firstText is null ? "\"\"" : EntityCodeGen.Verbatim(PropertyTypes.TextSample(firstText)),
        };
    }
}
