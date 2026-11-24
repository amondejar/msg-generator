using System.Text.RegularExpressions;

namespace Stratum.Core.Model;

/// <summary>Property types an entity can use, and how they map to C# and to test sample values.</summary>
public static partial class PropertyTypes
{
    public const int DefaultStringLength = 200;

    /// <summary>Text column of an enumeration (its longest value must fit).</summary>
    public const int EnumColumnLength = 64;

    private static readonly Dictionary<string, (string CSharp, string Sample)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["string"] = ("string", "\"sample\""),
        ["int"] = ("int", "1"),
        ["long"] = ("long", "1L"),
        ["decimal"] = ("decimal", "1.5m"),
        ["bool"] = ("bool", "true"),
        ["guid"] = ("Guid", "Guid.NewGuid()"),
        ["date"] = ("DateOnly", "new DateOnly(2026, 1, 1)"),
        ["datetime"] = ("DateTimeOffset", "new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)"),
    };

    private static readonly HashSet<string> ReservedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "CreatedAt", "UpdatedAt",
    };

    public static IReadOnlyCollection<string> All => Map.Keys;

    public const string EnumType = "enum";

    /// <summary>Type of an "object" list ("Items:OrderItem[]"); the item type is in <see cref="PropertyDefinition.Of"/>.</summary>
    public const string ObjectType = "object";

    /// <summary>
    /// How the property travels in integration events: enumerations as text, so subscribers don't depend on the
    /// publisher's enum (a new value doesn't break them); the items of an object list with their fields, so the
    /// subscriber doesn't need the publisher's item type.
    /// </summary>
    /// <param name="types">Item types of the publisher (needed for object lists).</param>
    public static PropertyDefinition OnTheWire(PropertyDefinition property, IReadOnlyList<ItemTypeDefinition>? types = null)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (property.Type == EnumType)
        {
            return property with { Type = "string", Enum = null };
        }

        if (property.Type == ObjectType && property.Fields is null)
        {
            var item = types?.FirstOrDefault(t => t.Name == property.Of)
                ?? throw new StratumException($"Falta el tipo {property.Of} de la lista {property.Name}.");
            return property with { Fields = [.. item.Properties.Select(p => OnTheWire(p))] };
        }

        return property;
    }

    /// <summary>
    /// The C# expression that puts <c>entity.Property</c> on the wire.
    /// </summary>
    /// <param name="eventType">Event class the value goes into (the items of an object list are nested records of it).</param>
    /// <param name="types">Item types of the service (needed for object lists).</param>
    public static string WireValue(PropertyDefinition property, string owner = "entity", string? eventType = null, IReadOnlyList<ItemTypeDefinition>? types = null)
    {
        ArgumentNullException.ThrowIfNull(property);
        var value = $"{owner}.{property.Name}";
        if (property.Type == ObjectType)
        {
            var item = types!.First(t => t.Name == property.Of);
            string Args(string owner) => string.Join(", ", item.Properties.Select(f => WireValue(f, owner)));
            return property.IsList ? $"[.. {value}.Select(i => new {eventType}.{property.Of}({Args("i")}))]"
                : property.Nullable ? $"{value} is {{ }} item ? new {eventType}.{property.Of}({Args("item")}) : null"
                : $"new {eventType}.{property.Of}({Args(value)})";
        }

        if (property.Type != EnumType)
        {
            return value;
        }

        return property.IsList ? $"[.. {value}.Select(v => v.ToString())]"
            : property.Nullable ? $"{value}?.ToString()" : $"{value}.ToString()";
    }

    /// <summary>Type of the item (or of the value, when it isn't a list).</summary>
    public static string ItemCSharp(PropertyDefinition property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.Type switch
        {
            EnumType => property.Enum!,
            ObjectType => property.Of!,
            _ => Map[property.Type].CSharp,
        };
    }

    /// <summary>C# type for parameters, records and contracts (lists as IReadOnlyList).</summary>
    /// <param name="eventType">For object lists in an event contract: the event class that nests the item record.</param>
    public static string ToCSharp(PropertyDefinition property, string? eventType = null)
    {
        ArgumentNullException.ThrowIfNull(property);
        var nested = property.Type == ObjectType && eventType is not null ? eventType + "." : string.Empty;
        if (property.IsList)
        {
            return $"IReadOnlyList<{nested}{ItemCSharp(property)}>";
        }

        return nested + ItemCSharp(property) + (property.Nullable ? "?" : string.Empty);
    }

    /// <summary>C# type of the property in the entity (lists as List, which EF Core maps).</summary>
    public static string EntityCSharp(PropertyDefinition property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.IsList ? $"List<{ItemCSharp(property)}>" : ToCSharp(property);
    }

    /// <summary>Initializer of the entity property (" = string.Empty;", " = [];" or nothing).</summary>
    public static string EntityInitializer(PropertyDefinition property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.IsList ? " = [];" : property.Type == "string" && !property.Nullable ? " = string.Empty;" : string.Empty;
    }

    private static readonly Dictionary<string, string> ChangedSamples = new(StringComparer.OrdinalIgnoreCase)
    {
        ["string"] = "\"changed\"",
        ["int"] = "2",
        ["long"] = "2L",
        ["decimal"] = "2.25m",
        ["bool"] = "false",
        ["guid"] = "Guid.NewGuid()",
        ["date"] = "new DateOnly(2026, 2, 2)",
        ["datetime"] = "new DateTimeOffset(2026, 2, 2, 8, 30, 0, TimeSpan.Zero)",
    };

    /// <summary>Sample value for tests: one item for lists.</summary>
    /// <param name="types">Item types of the service (an object list gets one item with sample values).</param>
    /// <param name="contract">For API requests: the items of object lists as their API contract (OrderItemContract).</param>
    /// <param name="changed">A second, different sample (optional fields null), to check that an update replaces.</param>
    public static string Sample(PropertyDefinition property, IReadOnlyList<ItemTypeDefinition>? types = null, bool contract = false, bool changed = false)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (property.IsObjectList)
        {
            var item = types?.FirstOrDefault(t => t.Name == property.Of);
            if (item is null)
            {
                return "[]";
            }

            return contract
                ? $"[new {ContractName(item.Name)}({string.Join(", ", item.Properties.Select(p => $"{p.Name}: {Sample(p, changed: changed)}"))})]"
                : $"[new {item.Name} {{ {string.Join(", ", item.Properties.Select(p => $"{p.Name} = {Sample(p, changed: changed)}"))} }}]";
        }

        if (changed && property.Nullable && !property.IsList)
        {
            return "null";
        }

        if (property.Type == ObjectType)
        {
            var item = types?.FirstOrDefault(t => t.Name == property.Of);
            return item is null ? "null!"
                : contract
                    ? $"new {ContractName(item.Name)}({string.Join(", ", item.Properties.Select(p => $"{p.Name}: {Sample(p, changed: changed)}"))})"
                    : $"new {item.Name} {{ {string.Join(", ", item.Properties.Select(p => $"{p.Name} = {Sample(p, changed: changed)}"))} }}";
        }

        var one = property.Type == EnumType
            ? changed ? $"Enum.GetValues<{property.Enum}>()[^1]" : $"default({property.Enum})"
            : changed ? ChangedSamples[property.Type] : Map[property.Type].Sample;
        return property.IsList ? $"[{one}]" : one;
    }

    /// <summary>API contract of an item type: OrderItem → OrderItemContract.</summary>
    public static string ContractName(string itemType) => itemType + "Contract";

    /// <summary>C# type of the property in the API contracts (requests and responses): object lists use the item's contract.</summary>
    public static string ApiCSharp(PropertyDefinition property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.IsObjectList ? $"IReadOnlyList<{ContractName(property.Of!)}>"
            : property.Type == ObjectType ? ContractName(property.Of!) + (property.Nullable ? "?" : string.Empty)
            : ToCSharp(property);
    }

    /// <summary>The value of a response field: object lists mapped to their contract.</summary>
    public static string ToApi(PropertyDefinition property, string value)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.IsObjectList ? $"[.. {value}.Select({ContractName(property.Of!)}.From)]" : value;
    }

    /// <summary>
    /// The value of a request field for the domain: an omitted list (JSON without the field) is an empty list, and the
    /// items of object lists go from their contract to the item type.
    /// </summary>
    public static string FromRequest(PropertyDefinition property, string value)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.IsObjectList ? $"[.. {value}?.Select(i => i.ToItem()) ?? []]"
            : property.IsList ? $"[.. {value} ?? []]"
            : property.Type == ObjectType ? $"{value}{(property.Nullable ? "?" : string.Empty)}.ToItem()"
            : value;
    }

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z][A-Za-z0-9]*)\s*:\s*(?<type>[A-Za-z][A-Za-z0-9]*)\s*(?<list>\[\s*\])?\s*(?<nullable>\?)?\s*$")]
    private static partial Regex PropertySpec();

    /// <summary>"Name:string?", or "CustomerId:Customer" for a reference: the inverse of <see cref="Parse"/>.</summary>
    public static string Spec(PropertyDefinition property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return $"{property.Name}:{property.References ?? property.Enum ?? property.Of ?? property.Type}{(property.IsList ? "[]" : string.Empty)}{(property.Nullable ? "?" : string.Empty)}";
    }

    public static string Spec(IEnumerable<PropertyDefinition> properties) => string.Join(", ", properties.Select(Spec));

    /// <summary>
    /// Parses <c>"Name:string, Price:decimal, Description:string?"</c>. A type in PascalCase that is not a basic
    /// type is a reference to that entity: "CustomerId:Customer" (or "Customer:Customer", which becomes CustomerId).
    /// </summary>
    /// <param name="enums">Enumerations of the service: a PascalCase type with one of these names is that enumeration.</param>
    /// <param name="types">Item types of the service, for lists of objects ("Items:OrderItem[]").</param>
    /// <param name="singleItems">Allow one item of an item type ("Item:OrderItem", parameters of operations).</param>
    public static IReadOnlyList<PropertyDefinition> Parse(string spec, IReadOnlyCollection<string>? enums = null, IReadOnlyCollection<string>? types = null, bool singleItems = false)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            throw new StratumException("Indica las propiedades con --props \"Nombre:tipo, ...\" (p. ej. \"Name:string, Price:decimal\").");
        }

        var result = new List<PropertyDefinition>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = PropertySpec().Match(part);
            if (!match.Success)
            {
                throw new StratumException($"Propiedad no válida: '{part}'. Formato: Nombre:tipo o Nombre:tipo? (opcional).");
            }

            var name = char.ToUpperInvariant(match.Groups["name"].Value[0]) + match.Groups["name"].Value[1..];
            var rawType = match.Groups["type"].Value;
            var type = rawType.ToLowerInvariant();
            var list = match.Groups["list"].Success;
            string? references = null;
            string? enumName = null;
            string? of = null;

            if (list && match.Groups["nullable"].Success)
            {
                throw new StratumException($"'{part}': una lista no puede ser opcional; sin elementos es una lista vacía. Quita el '?'.");
            }

            if (types?.FirstOrDefault(t => t == rawType) is { } typeMatch)
            {
                if (!list && !singleItems)
                {
                    throw new StratumException($"'{part}': {typeMatch} es un tipo de elemento de lista. Úsalo como lista: {name}:{typeMatch}[].");
                }

                of = typeMatch;
                type = ObjectType;
            }
            else if (enums?.FirstOrDefault(e => e == rawType) is { } enumMatch)
            {
                enumName = enumMatch;
                type = EnumType;
            }
            else if (!Map.ContainsKey(type))
            {
                if (list && char.IsUpper(rawType[0]))
                {
                    throw new StratumException($"'{part}': no hay ningún tipo de elemento '{rawType}'. Créalo antes (stratum type add {rawType} --props \"...\") o, si es otra entidad, usa una referencia en la entidad hija.");
                }

                if (!char.IsUpper(rawType[0]))
                {
                    throw new StratumException($"Tipo '{rawType}' no soportado en '{part}'. Tipos: {string.Join(", ", All)} (con [] para una lista: Tags:string[]), o el nombre de otra entidad del servicio para una referencia (CustomerId:Customer).");
                }

                references = rawType;
                type = "guid";
                if (!name.EndsWith("Id", StringComparison.Ordinal))
                {
                    name += "Id";
                }
            }

            if (ReservedProperties.Contains(name))
            {
                throw new StratumException($"'{name}' lo genera Stratum automáticamente (Id, CreatedAt, UpdatedAt). Quítalo de --props.");
            }

            if (result.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new StratumException($"Propiedad duplicada: '{name}'.");
            }

            result.Add(new PropertyDefinition
            {
                Name = name, Type = type, Nullable = match.Groups["nullable"].Success, References = references, Enum = enumName,
                List = list ? true : null, Of = of,
            });
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
