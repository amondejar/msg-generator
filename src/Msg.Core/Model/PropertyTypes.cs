using System.Text.RegularExpressions;

namespace Msg.Core.Model;

/// <summary>Property types an entity can use, and how they map to C# and to test sample values.</summary>
public static partial class PropertyTypes
{
    public const int DefaultStringLength = 200;

    private static readonly Dictionary<string, (string CSharp, string Sample)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["string"] = ("string", "\"sample\""),
        ["int"] = ("int", "1"),
        ["long"] = ("long", "1L"),
        ["decimal"] = ("decimal", "1.5m"),
        ["bool"] = ("bool", "true"),
        ["guid"] = ("Guid", "Guid.NewGuid()"),
        ["date"] = ("DateOnly", "new DateOnly(2026, 1, 1)"),
        ["datetime"] = ("DateTimeOffset", "DateTimeOffset.UtcNow"),
    };

    private static readonly HashSet<string> ReservedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "CreatedAt", "UpdatedAt",
    };

    public static IReadOnlyCollection<string> All => Map.Keys;

    public static string ToCSharp(PropertyDefinition property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return Map[property.Type].CSharp + (property.Nullable ? "?" : string.Empty);
    }

    public static string Sample(PropertyDefinition property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return Map[property.Type].Sample;
    }

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z][A-Za-z0-9]*)\s*:\s*(?<type>[A-Za-z]+)\s*(?<nullable>\?)?\s*$")]
    private static partial Regex PropertySpec();

    /// <summary>Parses <c>"Name:string, Price:decimal, Description:string?"</c>.</summary>
    public static IReadOnlyList<PropertyDefinition> Parse(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            throw new MsgException("Indica las propiedades con --props \"Nombre:tipo, ...\" (p. ej. \"Name:string, Price:decimal\").");
        }

        var result = new List<PropertyDefinition>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = PropertySpec().Match(part);
            if (!match.Success)
            {
                throw new MsgException($"Propiedad no válida: '{part}'. Formato: Nombre:tipo o Nombre:tipo? (opcional).");
            }

            var name = char.ToUpperInvariant(match.Groups["name"].Value[0]) + match.Groups["name"].Value[1..];
            var type = match.Groups["type"].Value.ToLowerInvariant();

            if (!Map.ContainsKey(type))
            {
                throw new MsgException($"Tipo '{type}' no soportado en '{part}'. Tipos: {string.Join(", ", All)}.");
            }

            if (ReservedProperties.Contains(name))
            {
                throw new MsgException($"'{name}' lo genera Stratum automáticamente (Id, CreatedAt, UpdatedAt). Quítalo de --props.");
            }

            if (result.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new MsgException($"Propiedad duplicada: '{name}'.");
            }

            result.Add(new PropertyDefinition { Name = name, Type = type, Nullable = match.Groups["nullable"].Success });
        }

        return result;
    }

    /// <summary>Naive English pluralization; override with --plural when it is wrong.</summary>
    public static string Pluralize(string singular)
    {
        ArgumentException.ThrowIfNullOrEmpty(singular);

        if (singular.EndsWith('y') && singular.Length > 1 && !"aeiou".Contains(singular[^2], StringComparison.Ordinal))
        {
            return singular[..^1] + "ies";
        }

        if (singular.EndsWith('s') || singular.EndsWith('x') || singular.EndsWith('z')
            || singular.EndsWith("ch", StringComparison.Ordinal) || singular.EndsWith("sh", StringComparison.Ordinal))
        {
            return singular + "es";
        }

        return singular + "s";
    }
}
