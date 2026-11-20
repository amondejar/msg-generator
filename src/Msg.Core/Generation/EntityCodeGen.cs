using System.Text.RegularExpressions;
using Msg.Core.Model;

namespace Msg.Core.Generation;

/// <summary>
/// Code fragments that depend on an entity's properties. The template engine has no loops on purpose:
/// lists are rendered here, in C#, and injected into the templates as tokens.
/// </summary>
internal static partial class EntityCodeGen
{
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    [GeneratedRegex("(?<=[a-z0-9])([A-Z])")]
    private static partial Regex WordBoundary();

    public static bool HasRequiredString(EntityDefinition entity) =>
        entity.Properties.Any(p => IsString(p) && !p.Nullable);

    /// <summary>Tokens for one entity (merged with the service tokens).</summary>
    public static Dictionary<string, string> Tokens(EntityDefinition entity, FeatureSet features)
    {
        var props = entity.Properties;
        var max = PropertyTypes.DefaultStringLength;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Entity"] = entity.Name,
            ["Entities"] = entity.Plural,
            ["entities-kebab"] = WordBoundary().Replace(entity.Plural, "-$1").ToLowerInvariant(),
            ["entities_snake"] = WordBoundary().Replace(entity.Plural, "_$1").ToLowerInvariant(),
            ["entity-kebab"] = WordBoundary().Replace(entity.Name, "-$1").ToLowerInvariant(),
            ["IntegrationEventParameters"] = string.Join(", ", props.Select(p => $"{PropertyTypes.ToCSharp(p)} {p.Name}")),

            // --- shared / vertical slice ---
            ["EntityProperties"] = string.Join("\n\n", props.Select(p =>
                $"    public {PropertyTypes.ToCSharp(p)} {p.Name} {{ get; set; }}" +
                (IsString(p) && !p.Nullable ? " = string.Empty;" : string.Empty))),
            ["PropertyConfigurations"] = string.Join("\n", props.Select(Configuration).Where(l => l.Length > 0)),
            ["RequestParameters"] = string.Join(",\n", props.Select(p => $"    {ValidationAttributes(p)}{PropertyTypes.ToCSharp(p)} {p.Name}")),
            ["ResponseParameters"] = string.Join("\n", props.Select(p => $"    {PropertyTypes.ToCSharp(p)} {p.Name},")),
            ["ResponseArguments"] = string.Join("\n", props.Select(p => $"            entity.{p.Name},")),
            ["CreateAssignments"] = string.Join("\n", props.Select(p => $"            {p.Name} = request.{p.Name},")),
            ["UpdateAssignments"] = string.Join("\n", props.Select(p => $"        entity.{p.Name} = request.{p.Name};")),
            ["RequestToEntity"] = string.Join("\n", props.Select(p => $"        {p.Name} = {p.Name},")),
            ["RequestApplyTo"] = string.Join("\n", props.Select(p => $"        entity.{p.Name} = {p.Name};")),
            ["SampleArguments"] = string.Join(",\n", props.Select(p => $"        {p.Name}: {PropertyTypes.Sample(p)}")),
            ["TestClient"] = features.Auth ? "factory.CreateAuthenticatedClient()" : "factory.CreateClient()",

            // --- clean + DDD ---
            ["AggregateProperties"] = string.Join("\n\n", props.Select(p =>
                $"    public {PropertyTypes.ToCSharp(p)} {p.Name} {{ get; private set; }}" +
                (IsString(p) && !p.Nullable ? " = string.Empty;" : string.Empty))),
            ["FactoryParameters"] = string.Join(", ", props.Select(p => $"{PropertyTypes.ToCSharp(p)} {Camel(p.Name)}")),
            ["FactoryArguments"] = string.Join(", ", props.Select(p => Camel(p.Name))),
            ["ApplyGuards"] = string.Join("\n\n", props.Where(IsString).Select(p => Guard(p, max))),
            ["ApplyAssignments"] = string.Join("\n", props.Select(p => $"        {p.Name} = {Camel(p.Name)};")),
            ["CommandParameters"] = string.Join(", ", props.Select(p => $"{PropertyTypes.ToCSharp(p)} {p.Name}")),
            ["CommandArguments"] = string.Join(", ", props.Select(p => $"command.{p.Name}")),
            ["RequestToCommand"] = string.Join(", ", props.Select(p => $"request.{p.Name}")),
            ["EntityArguments"] = string.Join(", ", props.Select(p => $"entity.{p.Name}")),
            ["DomainSampleArguments"] = string.Join(", ", props.Select(PropertyTypes.Sample)),
            ["DomainInvalidArguments"] = InvalidArguments(props),
        };
    }

    /// <summary>Service-level tokens listing every entity (registration, DbSets, repositories).</summary>
    public static Dictionary<string, string> ServiceTokens(ServiceDefinition definition)
    {
        var service = definition.Service.Name;
        var entities = definition.Entities;
        var clean = definition.Architecture == Architectures.CleanDdd;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FeatureUsings"] = string.Join("\n", entities.Select(e => $"using {service}.Api.Features.{e.Plural};")),
            ["FeatureMaps"] = string.Join("\n", entities.Select(e => $"        api.Map{e.Name}Endpoints();")),
            ["DbSets"] = string.Join("\n\n", entities.Select(e => $"    public DbSet<{e.Name}> {e.Plural} => Set<{e.Name}>();")),
            ["DomainUsings"] = string.Join("\n", entities.Select(e => $"using {service}.Domain.{e.Plural};")),
            ["InfrastructureUsings"] = string.Join("\n", entities.Select(e => $"using {service}.Infrastructure.Persistence.{e.Plural};")),
            ["RepositoryRegistrations"] = string.Join("\n", entities.Select(e => $"        services.AddScoped<I{e.Name}Repository, {e.Name}Repository>();")),
            ["DbContextNamespace"] = clean ? $"{service}.Infrastructure.Persistence" : $"{service}.Api.Data",
        };
    }

    private static bool IsString(PropertyDefinition p) => p.Type.Equals("string", StringComparison.OrdinalIgnoreCase);

    internal static string Camel(string name)
    {
        var camel = char.ToLowerInvariant(name[0]) + name[1..];
        return CSharpKeywords.Contains(camel) ? "@" + camel : camel;
    }

    private static string Guard(PropertyDefinition p, int max)
    {
        var arg = Camel(p.Name);
        var lines = new List<string>();
        if (!p.Nullable)
        {
            lines.Add($"        if (string.IsNullOrWhiteSpace({arg}))\n        {{\n            throw new DomainException(\"{p.Name} is required.\");\n        }}");
        }

        var lengthCheck = p.Nullable ? $"{arg} is {{ Length: > {max} }}" : $"{arg}.Length > {max}";
        lines.Add($"        if ({lengthCheck})\n        {{\n            throw new DomainException(\"{p.Name} must be at most {max} characters.\");\n        }}");
        return string.Join("\n\n", lines);
    }

    private static string InvalidArguments(IReadOnlyList<PropertyDefinition> props)
    {
        var firstRequired = props.FirstOrDefault(p => IsString(p) && !p.Nullable);
        return string.Join(", ", props.Select(p => p == firstRequired ? "\" \"" : PropertyTypes.Sample(p)));
    }

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
