using System.Text.RegularExpressions;
using Msg.Core.Model;

namespace Msg.Core.Generation;

/// <summary>
/// Code fragments that depend on an entity's properties. The template engine has no loops on purpose:
/// lists are rendered here, in C#, and injected into the templates as tokens.
/// </summary>
internal static partial class EntityCodeGen
{
    [GeneratedRegex("(?<=[a-z0-9])([A-Z])")]
    private static partial Regex WordBoundary();

    public static bool HasRequiredString(EntityDefinition entity) =>
        entity.Properties.Any(p => IsString(p) && !p.Nullable);

    /// <summary>Tokens for one entity (merged with the service tokens).</summary>
    public static Dictionary<string, string> Tokens(EntityDefinition entity, FeatureSet features)
    {
        var props = entity.Properties;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Entity"] = entity.Name,
            ["Entities"] = entity.Plural,
            ["entities-kebab"] = WordBoundary().Replace(entity.Plural, "-$1").ToLowerInvariant(),
            ["entities_snake"] = WordBoundary().Replace(entity.Plural, "_$1").ToLowerInvariant(),

            ["EntityProperties"] = string.Join("\n\n", props.Select(p =>
                $"    public {PropertyTypes.ToCSharp(p)} {p.Name} {{ get; set; }}" +
                (IsString(p) && !p.Nullable ? " = string.Empty;" : string.Empty))),

            ["PropertyConfigurations"] = string.Join("\n", props.Select(Configuration).Where(l => l.Length > 0)),

            ["RequestParameters"] = string.Join(",\n", props.Select(p => $"    {ValidationAttributes(p)}{PropertyTypes.ToCSharp(p)} {p.Name}")),

            ["ResponseParameters"] = string.Join("\n", props.Select(p => $"    {PropertyTypes.ToCSharp(p)} {p.Name},")),

            ["ResponseArguments"] = string.Join("\n", props.Select(p => $"            entity.{p.Name},")),

            ["CreateAssignments"] = string.Join("\n", props.Select(p => $"            {p.Name} = request.{p.Name},")),

            ["UpdateAssignments"] = string.Join("\n", props.Select(p => $"        entity.{p.Name} = request.{p.Name};")),

            ["SampleArguments"] = string.Join(",\n", props.Select(p => $"        {p.Name}: {PropertyTypes.Sample(p)}")),

            ["TestClient"] = features.Auth ? "factory.CreateAuthenticatedClient()" : "factory.CreateClient()",
        };
    }

    /// <summary>Service-level tokens listing every entity (registration, DbSets).</summary>
    public static Dictionary<string, string> ServiceTokens(ServiceDefinition definition)
    {
        var service = definition.Service.Name;
        var entities = definition.Entities;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FeatureUsings"] = string.Join("\n", entities.Select(e => $"using {service}.Api.Features.{e.Plural};")),
            ["FeatureMaps"] = string.Join("\n", entities.Select(e => $"        api.Map{e.Name}Endpoints();")),
            ["DbSets"] = string.Join("\n\n", entities.Select(e => $"    public DbSet<{e.Name}> {e.Plural} => Set<{e.Name}>();")),
        };
    }

    private static bool IsString(PropertyDefinition p) => p.Type.Equals("string", StringComparison.OrdinalIgnoreCase);

    private static string ValidationAttributes(PropertyDefinition p)
    {
        if (!IsString(p))
        {
            return string.Empty;
        }

        return p.Nullable
            ? $"[property: MaxLength({PropertyTypes.DefaultStringLength})] "
            : $"[property: Required, MaxLength({PropertyTypes.DefaultStringLength})] ";
    }

    private static string Configuration(PropertyDefinition p) => p.Type.ToLowerInvariant() switch
    {
        "string" => $"        builder.Property(e => e.{p.Name}).HasMaxLength({PropertyTypes.DefaultStringLength})" +
                    (p.Nullable ? ";" : ".IsRequired();"),
        "decimal" => $"        builder.Property(e => e.{p.Name}).HasPrecision(18, 2);",
        _ => string.Empty,
    };
}
