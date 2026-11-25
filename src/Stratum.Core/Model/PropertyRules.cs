using System.Globalization;
using System.Text.RegularExpressions;

namespace Stratum.Core.Model;

/// <summary>
/// What a valid value looks like ("rules" of a property in stratum.json). Texts: length, format, pattern (with an
/// example that meets it, for the tests and the .http file). Numbers (int, long, decimal): minimum and maximum.
/// </summary>
public sealed record PropertyRules
{
    /// <summary>Texts: at least this many characters.</summary>
    public int? MinLength { get; init; }

    /// <summary>Texts: at most this many characters (also the column length). Null: 200.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Texts: one of <see cref="TextFormats"/> (email, url, phone).</summary>
    public string? Format { get; init; }

    /// <summary>Texts: a regular expression the whole value must match (e.g. ^\d{5}$).</summary>
    public string? Pattern { get; init; }

    /// <summary>Texts: a valid value, used by the tests, the .http file and the documentation. Required with a pattern.</summary>
    public string? Example { get; init; }

    /// <summary>Numbers: the smallest value allowed.</summary>
    public decimal? Min { get; init; }

    /// <summary>Numbers: the largest value allowed.</summary>
    public decimal? Max { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => MinLength is null && MaxLength is null && Format is null && Pattern is null && Example is null && Min is null && Max is null;

    /// <summary>Null when nothing is set (so stratum.json doesn't get an empty "rules").</summary>
    public PropertyRules? OrNull() => IsEmpty ? null : this;
}

/// <summary>Formats a text property can have.</summary>
public static class TextFormats
{
    public const string Email = "email";
    public const string Url = "url";
    public const string Phone = "phone";

    public static readonly string[] All = [Email, Url, Phone];
}

/// <summary>Reading and checking <see cref="PropertyRules"/>.</summary>
public static class ValueRules
{
    /// <summary>Longest text allowed: also the length of the column.</summary>
    public const int MaxTextLength = 100_000;

    /// <summary>Column length of a text property: its rule, or 200.</summary>
    public static int MaxLength(PropertyDefinition property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.Rules?.MaxLength ?? PropertyTypes.DefaultStringLength;
    }

    public static bool IsText(PropertyDefinition p) => !p.IsList && p.Type == "string" && p.References is null;

    public static bool IsNumber(PropertyDefinition p) => !p.IsList && p.Type is "int" or "long" or "decimal";

    /// <summary>The problems of the rules of a property (empty when they're fine).</summary>
    public static IEnumerable<string> Problems(PropertyDefinition p, string owner)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.Rules is not { } r)
        {
            yield break;
        }

        var where = $"{owner}.{p.Name}";
        var text = IsText(p);
        var number = IsNumber(p);
        if (!text && (r.MinLength is not null || r.MaxLength is not null || r.Format is not null || r.Pattern is not null || r.Example is not null))
        {
            yield return $"{where}: la longitud, el formato, el patrón y el ejemplo son para textos (string), no para {Describe(p)}.";
        }

        if (!number && (r.Min is not null || r.Max is not null))
        {
            yield return $"{where}: el mínimo y el máximo son para números (int, long, decimal), no para {Describe(p)}.";
        }

        if (r.MinLength is < 0 || r.MaxLength is < 1 || r.MaxLength > MaxTextLength)
        {
            yield return $"{where}: la longitud debe estar entre 0 y {MaxTextLength} (el máximo, al menos 1).";
        }

        if (r.MinLength is { } min && r.MaxLength is { } max && min > max)
        {
            yield return $"{where}: la longitud mínima ({min}) es mayor que la máxima ({max}).";
        }

        if (r.MinLength is { } minAlone && r.MaxLength is null && minAlone > PropertyTypes.DefaultStringLength)
        {
            yield return $"{where}: la longitud mínima ({minAlone}) es mayor que la máxima por defecto ({PropertyTypes.DefaultStringLength}); indica también la máxima.";
        }

        if (r.Format is { } format && !TextFormats.All.Contains(format))
        {
            yield return $"{where}: formato '{format}' desconocido. Usa {string.Join(", ", TextFormats.All)}.";
        }

        if (r.Pattern is { } pattern && !IsValidPattern(pattern))
        {
            yield return $"{where}: el patrón '{pattern}' no es una expresión regular válida.";
        }

        if (r.Pattern is not null && r.Example is null)
        {
            yield return $"{where}: con un patrón, indica también un ejemplo que lo cumpla (lo usan los tests y el .http).";
        }

        if (r.Example is { } example && text && ExampleProblem(p, example) is { } problem)
        {
            yield return $"{where}: el ejemplo '{example}' no es válido: {problem}.";
        }

        // Without an example, the tests and the .http use one Stratum makes up: it must meet the rules too.
        if (r.Example is null && r.Pattern is null && text && r.Format is not null
            && (ExampleProblem(p, PropertyTypes.TextSample(p)) is not null || ExampleProblem(p, PropertyTypes.TextSample(p, changed: true)) is not null))
        {
            yield return $"{where}: con esas reglas, indica un ejemplo válido (lo usan los tests y el .http).";
        }

        if (r.Min is { } low && r.Max is { } high && low > high)
        {
            yield return $"{where}: el mínimo ({low.ToString(CultureInfo.InvariantCulture)}) es mayor que el máximo ({high.ToString(CultureInfo.InvariantCulture)}).";
        }

        if (number && p.Type == "int" && (r.Min is < int.MinValue or > int.MaxValue || r.Max is < int.MinValue or > int.MaxValue))
        {
            yield return $"{where}: el mínimo y el máximo de un int deben caber en un int.";
        }

        if (number && p.Type is "int" or "long" && (r.Min is { } m1 && m1 != decimal.Truncate(m1) || r.Max is { } m2 && m2 != decimal.Truncate(m2)))
        {
            yield return $"{where}: el mínimo y el máximo de un {p.Type} deben ser enteros.";
        }
    }

    /// <summary>Why <paramref name="value"/> doesn't meet the rules of a text property, or null when it does.</summary>
    public static string? ExampleProblem(PropertyDefinition p, string value)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(value);
        var r = p.Rules ?? new PropertyRules();
        if (value.Length > MaxLength(p))
        {
            return $"tiene {value.Length} caracteres y el máximo es {MaxLength(p)}";
        }

        if (r.MinLength is { } min && value.Length < min)
        {
            return $"tiene {value.Length} caracteres y el mínimo es {min}";
        }

        if (r.Pattern is { } pattern && IsValidPattern(pattern) && !Regex.IsMatch(value, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            return "no cumple el patrón";
        }

        return r.Format switch
        {
            TextFormats.Email when !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(value) => "no es un email",
            TextFormats.Url when !new System.ComponentModel.DataAnnotations.UrlAttribute().IsValid(value) => "no es una URL (http:// o https://)",
            TextFormats.Phone when !new System.ComponentModel.DataAnnotations.PhoneAttribute().IsValid(value) => "no es un teléfono",
            _ => null,
        };
    }

    /// <summary>"máx. 100 caracteres, email" — for the Studio, the specification and the CLI.</summary>
    public static string Describe(PropertyRules? r, PropertyDefinition p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var parts = new List<string>();
        if (IsText(p))
        {
            parts.Add(r?.MinLength is { } min and > 0 ? min == MaxLength(p) ? $"{min} caracteres" : $"{min}–{MaxLength(p)} caracteres" : $"hasta {MaxLength(p)} caracteres");
            if (r?.Format is { } format)
            {
                parts.Add(format switch { TextFormats.Email => "email", TextFormats.Url => "URL", TextFormats.Phone => "teléfono", _ => format });
            }

            if (r?.Pattern is { } pattern)
            {
                parts.Add($"patrón {pattern}");
            }

            if (r?.Example is { } example)
            {
                parts.Add($"ej. {example}");
            }
        }

        if (IsNumber(p) && (r?.Min is not null || r?.Max is not null))
        {
            parts.Add(r.Min is { } low && r.Max is { } high ? $"entre {Number(low)} y {Number(high)}"
                : r.Min is { } onlyLow ? $"mínimo {Number(onlyLow)}" : $"máximo {Number(r.Max!.Value)}");
        }

        return string.Join(", ", parts);
    }

    public static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool IsValidPattern(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Describe(PropertyDefinition p) =>
        p.IsList ? "una lista" : p.References is not null ? "una referencia" : p.Type == PropertyTypes.EnumType ? "un enumerado" : p.Type;
}
