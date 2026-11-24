using System.Text.RegularExpressions;
using Stratum.Core;
using Stratum.Core.Model;

namespace Stratum.Studio.State;

/// <summary>
/// What the entity dialogs edit: a copy of a property, operation, enumeration or item type. Nothing reaches the draft
/// until "Guardar y cerrar" (<c>ApplyTo</c>); "Cerrar" just drops the copy.
/// </summary>
public static partial class EntityEditors
{
    /// <summary>Basic types offered in the type selectors.</summary>
    public static readonly string[] BasicTypes = ["string", "int", "long", "decimal", "bool", "guid", "date", "datetime"];

    public const string One = "one";
    public const string Optional = "optional";
    public const string List = "list";

    /// <summary>Names the generated code already uses on every entity.</summary>
    private static readonly string[] Reserved = ["Id", "CreatedAt", "UpdatedAt"];

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex Identifier();

    /// <summary>Null if the name is a valid C# identifier for Stratum (letters and digits); else why not.</summary>
    public static string? CheckName(string? name, string what)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? $"Ponle nombre {what}."
            : !Identifier().IsMatch(trimmed) ? $"«{trimmed}» no vale como nombre {what}: solo letras y números, empezando por letra (p. ej. UnitPrice)."
            : null;
    }

    internal static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A row of a list of fields: parameters of an operation, properties of an item type.</summary>
    public sealed class FieldRow
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>A basic type, an enumeration or an item type, by name.</summary>
        public string Type { get; set; } = "string";

        /// <summary><see cref="One"/>, <see cref="Optional"/> or <see cref="List"/>.</summary>
        public string Kind { get; set; } = One;

        public bool IsBlank => string.IsNullOrWhiteSpace(Name);
    }

    /// <summary>"Reason:string, Codes:string[], Note:string?" → rows (tolerant: what can't be read stays as typed).</summary>
    public static List<FieldRow> ParseRows(string? spec)
    {
        var rows = new List<FieldRow>();
        foreach (var part in (spec ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            var name = colon < 0 ? part : part[..colon].Trim();
            var type = colon < 0 ? "string" : part[(colon + 1)..].Trim();
            var kind = One;
            if (type.EndsWith('?'))
            {
                kind = Optional;
                type = type[..^1].TrimEnd();
            }

            if (type.EndsWith("[]", StringComparison.Ordinal))
            {
                kind = List;
                type = type[..^2].TrimEnd();
            }

            rows.Add(new FieldRow { Name = name, Type = type.Length == 0 ? "string" : type, Kind = kind });
        }

        return rows;
    }

    /// <summary>Rows → "Name:type, Codes:string[], Note:string?" (blank rows are skipped).</summary>
    public static string ComposeRows(IEnumerable<FieldRow> rows) =>
        string.Join(", ", rows.Where(r => !r.IsBlank).Select(r =>
            $"{r.Name.Trim()}:{r.Type.Trim()}{(r.Kind == List ? "[]" : string.Empty)}{(r.Kind == Optional ? "?" : string.Empty)}"));

    private static string? CheckRows(IReadOnlyList<FieldRow> rows, string what)
    {
        foreach (var row in rows.Where(r => !r.IsBlank))
        {
            if (CheckName(row.Name, what) is { } error)
            {
                return error;
            }
        }

        var duplicate = rows.Where(r => !r.IsBlank).GroupBy(r => r.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null ? null : $"«{duplicate.Key}» está repetido.";
    }

    /// <summary>A property of an entity.</summary>
    public sealed class PropertyEditor
    {
        /// <summary>The property being edited; null for a new one.</summary>
        public PropertyDraft? Original { get; init; }

        public string Name { get; set; } = string.Empty;

        /// <summary>Same encoding as <see cref="PropertyDraft.Type"/>: "string", "#Enum", "*ItemType", "@Entity".</summary>
        public string Type { get; set; } = "string";

        public string Kind { get; set; } = One;

        public bool IsItem => Type.StartsWith(PropertyDraft.ItemPrefix, StringComparison.Ordinal);

        public bool IsReference => Type.StartsWith(PropertyDraft.ReferencePrefix, StringComparison.Ordinal);

        /// <summary>What Kind really is: lists of objects are always lists, references never.</summary>
        public string EffectiveKind => IsItem ? List : IsReference && Kind == List ? One : Kind;

        public static PropertyEditor New(EntityDraft entity) =>
            new() { Name = string.Empty, Type = "string", Kind = One };

        public static PropertyEditor For(PropertyDraft property) => new()
        {
            Original = property,
            Name = property.Name,
            Type = property.Type,
            Kind = property.IsList ? List : property.Nullable ? Optional : One,
        };

        public string? Validate(EntityDraft entity)
        {
            if (CheckName(Name, "a la propiedad") is { } error)
            {
                return error;
            }

            if (Reserved.Any(r => Same(r, Name)))
            {
                return $"{Name.Trim()} ya lo genera Stratum en todas las entidades.";
            }

            return entity.Properties.Any(p => p != Original && Same(p.Name, Name)) ? $"{entity.Name} ya tiene una propiedad {Name.Trim()}." : null;
        }

        /// <summary>The draft this editor turns into (the same object when editing: renames keep their original name).</summary>
        public PropertyDraft ApplyTo(EntityDraft entity)
        {
            var target = Original ?? new PropertyDraft();
            target.Name = Name.Trim();
            target.Type = Type;
            target.List = EffectiveKind == List;
            target.Nullable = EffectiveKind == Optional;
            if (Original is null)
            {
                entity.Properties.Add(target);
            }

            return target;
        }

        /// <summary>A preview of the property as it would be written with --props.</summary>
        public string Spec => $"{(Name.Trim().Length == 0 ? "?" : Name.Trim())}:{TypeName(Type)}{(EffectiveKind == List ? "[]" : string.Empty)}{(EffectiveKind == Optional ? "?" : string.Empty)}";
    }

    /// <summary>What Stratum generates for a property (shown next to it).</summary>
    public static string Generates(PropertyDraft p) => p.Item is { } item
        ? $"Tabla propia de {item} · se reemplaza entera"
        : p.IsList
        ? (p.Enum is not null ? "Lista de textos" : "Columna JSON · vacía por defecto")
        : p.References is { } target
        ? $"Clave ajena a {target} · se comprueba que existe" + (p.Nullable ? " · opcional" : string.Empty)
        : p.Enum is { } en
        ? "Texto en la BD · valor desconocido → 400" + (p.Nullable ? " · opcional" : string.Empty)
        : (p.Type, p.Nullable) switch
    {
        ("string", false) => $"Required · MaxLength({PropertyTypes.DefaultStringLength})",
        ("string", true) => $"MaxLength({PropertyTypes.DefaultStringLength}) · columna nullable",
        ("decimal", _) => "Precisión (18, 2)" + (p.Nullable ? " · nullable" : string.Empty),
        (_, true) => "Columna nullable",
        _ => "Columna obligatoria",
    };

    /// <summary>"#OrderStatus" → "OrderStatus"; "string" → "string".</summary>
    public static string TypeName(string type) =>
        type.Length > 0 && (type[0] == PropertyDraft.EnumPrefix[0] || type[0] == PropertyDraft.ItemPrefix[0] || type[0] == PropertyDraft.ReferencePrefix[0]) ? type[1..] : type;

    /// <summary>A business operation of an aggregate (clean-ddd).</summary>
    public sealed class OperationEditor
    {
        public OperationDraft? Original { get; init; }

        public string Name { get; set; } = string.Empty;

        public string Event { get; set; } = string.Empty;

        public bool Publish { get; set; }

        public List<FieldRow> Parameters { get; init; } = [];

        public static OperationEditor For(OperationDraft operation) => new()
        {
            Original = operation,
            Name = operation.Name,
            Event = operation.Event,
            Publish = operation.Publish,
            Parameters = ParseRows(operation.Parameters),
        };

        public string ParameterSpec => ComposeRows(Parameters);

        public string? Validate(EntityDraft entity, IReadOnlyCollection<string> enums, IReadOnlyCollection<string> types)
        {
            if (CheckName(Name, "a la operación") is { } error)
            {
                return error;
            }

            if (entity.Operations.Any(o => o != Original && Same(o.Name, Name)))
            {
                return $"{entity.Name} ya tiene una operación {Name.Trim()}.";
            }

            if (!string.IsNullOrWhiteSpace(Event) && CheckName(Event, "al evento") is { } eventError)
            {
                return eventError;
            }

            if (Publish && !string.IsNullOrWhiteSpace(Event) && !Event.Trim().StartsWith(entity.Name, StringComparison.Ordinal))
            {
                return $"Para publicarlo, el evento tiene que empezar por {entity.Name} (p. ej. {entity.Name}{Event.Trim()}).";
            }

            if (CheckRows(Parameters, "al parámetro") is { } rowError)
            {
                return rowError;
            }

            try
            {
                if (ParameterSpec.Length > 0)
                {
                    PropertyTypes.Parse(ParameterSpec, enums, types, singleItems: true);
                }
            }
            catch (StratumException e)
            {
                return e.Message;
            }

            return null;
        }

        public OperationDraft ApplyTo(EntityDraft entity)
        {
            var target = Original ?? new OperationDraft();
            target.Name = Name.Trim();
            target.Event = Event.Trim();
            target.Publish = Publish;
            target.Parameters = ParameterSpec;
            if (Original is null)
            {
                entity.Operations.Add(target);
            }

            return target;
        }
    }

    /// <summary>An enumeration of the service.</summary>
    public sealed class EnumEditor
    {
        public EnumDraft? Original { get; init; }

        public string Name { get; set; } = string.Empty;

        public List<string> Values { get; init; } = [];

        public static EnumEditor New() => new() { Values = ["", ""] };

        public static EnumEditor For(EnumDraft draft) => new()
        {
            Original = draft,
            Name = draft.Name,
            Values = [.. draft.Values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        };

        public IEnumerable<string> FilledValues => Values.Select(v => v.Trim()).Where(v => v.Length > 0);

        public string? Validate(ServiceDraft draft)
        {
            if (CheckName(Name, "al enumerado") is { } error)
            {
                return error;
            }

            if (draft.Enums.Any(e => e != Original && Same(e.Name, Name)) || draft.Types.Any(t => Same(t.Name, Name)) || draft.Entities.Any(e => Same(e.Name, Name)))
            {
                return $"Ya hay algo que se llama {Name.Trim()} en el servicio.";
            }

            if (!FilledValues.Any())
            {
                return "Añade al menos un valor.";
            }

            foreach (var value in FilledValues)
            {
                if (CheckName(value, "a un valor") is { } valueError)
                {
                    return valueError;
                }
            }

            var duplicate = FilledValues.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            return duplicate is null ? null : $"El valor {duplicate.Key} está repetido.";
        }

        /// <summary>Values that were saved before and are gone now (rows in the database may still hold them).</summary>
        public IReadOnlyList<string> RemovedValues =>
            Original is null ? [] : [.. Original.Values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => !FilledValues.Contains(v, StringComparer.Ordinal))];

        public EnumDraft ApplyTo(ServiceDraft draft)
        {
            var target = Original ?? new EnumDraft();
            target.Name = Name.Trim();
            target.Values = string.Join(", ", FilledValues);
            if (Original is null)
            {
                draft.Enums.Add(target);
            }

            return target;
        }
    }

    /// <summary>An item type (the elements of a list of objects).</summary>
    public sealed class TypeEditor
    {
        public TypeDraft? Original { get; init; }

        public string Name { get; set; } = string.Empty;

        public List<FieldRow> Properties { get; init; } = [];

        public static TypeEditor New() => new() { Properties = [new FieldRow()] };

        public static TypeEditor For(TypeDraft draft) => new() { Original = draft, Name = draft.Name, Properties = ParseRows(draft.Properties) };

        public string Spec => ComposeRows(Properties);

        public string? Validate(ServiceDraft draft, IReadOnlyCollection<string> enums)
        {
            if (CheckName(Name, "al tipo") is { } error)
            {
                return error;
            }

            if (draft.Types.Any(t => t != Original && Same(t.Name, Name)) || draft.Enums.Any(e => Same(e.Name, Name)) || draft.Entities.Any(e => Same(e.Name, Name)))
            {
                return $"Ya hay algo que se llama {Name.Trim()} en el servicio.";
            }

            if (Properties.All(r => r.IsBlank))
            {
                return "Añade al menos una propiedad.";
            }

            if (CheckRows(Properties, "a la propiedad") is { } rowError)
            {
                return rowError;
            }

            try
            {
                PropertyTypes.Parse(Spec, enums);
            }
            catch (StratumException e)
            {
                return e.Message;
            }

            return null;
        }

        public TypeDraft ApplyTo(ServiceDraft draft)
        {
            var target = Original ?? new TypeDraft();
            target.Name = Name.Trim();
            target.Properties = Spec;
            if (Original is null)
            {
                draft.Types.Add(target);
            }

            return target;
        }
    }
}
