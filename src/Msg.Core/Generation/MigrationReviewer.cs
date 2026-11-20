using System.Text;
using System.Text.RegularExpressions;
using Msg.Core.Model;

namespace Msg.Core.Generation;

public enum FindingLevel
{
    /// <summary>Nothing to worry about (e.g. a rename Stratum fixed).</summary>
    Info,

    /// <summary>Can fail or change data with some rows (type change, column made required...).</summary>
    Warning,

    /// <summary>Deletes data: a dropped table or column.</summary>
    Danger,
}

public sealed record MigrationFinding(FindingLevel Level, string Message);

/// <summary>Result of reviewing one EF Core migration.</summary>
/// <param name="Source">The migration source after Stratum's fixes (equal to the original when <paramref name="Changed"/> is false).</param>
/// <param name="Renames">Renames from msg.json that this migration now performs (to restore them if the migration is removed).</param>
public sealed record MigrationReview(
    string Name,
    string Path,
    IReadOnlyList<MigrationFinding> Findings,
    IReadOnlyList<PropertyRename> Renames,
    bool Changed,
    string Source)
{
    public bool DeletesData => Findings.Any(f => f.Level == FindingLevel.Danger);
}

/// <summary>
/// Reviews the migrations that "dotnet ef migrations add" scaffolds: turns the renames recorded in msg.json
/// into RenameColumn (EF Core would drop the column and add a new one, losing its data) and reports every
/// operation that deletes or may break data.
/// </summary>
public static partial class MigrationReviewer
{
    /// <summary>Where the migrations live: the Api (vertical slice) or the Infrastructure project (clean + DDD).</summary>
    public static string MigrationsProject(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var service = definition.Service.Name;
        return definition.Architecture == Architectures.CleanDdd ? $"src/{service}.Infrastructure" : $"src/{service}.Api";
    }

    /// <summary>Migration names (file names without .cs: "20261120061500_AddProduct"), oldest first.</summary>
    public static IReadOnlyList<string> List(string serviceDirectory, ServiceDefinition definition)
    {
        var folder = Path.Combine(serviceDirectory, MigrationsProject(definition), "Migrations");
        return Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*.cs")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(f => !f.EndsWith(".Designer", StringComparison.Ordinal) && !f.EndsWith("ModelSnapshot", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>
    /// Reviews the newest migration of the service. With <paramref name="apply"/>, writes the fixed migration and
    /// clears pendingRenames in msg.json (they are now in the migration, or reported as not found).
    /// </summary>
    public static MigrationReview ReviewLatest(string serviceDirectory, bool apply)
    {
        var definition = ServicePlanner.Load(serviceDirectory);
        var migrations = List(serviceDirectory, definition);
        if (migrations.Count == 0)
        {
            throw new MsgException("El servicio no tiene migraciones. Crea una con 'dotnet ef migrations add <Nombre>' (o desde el Studio).");
        }

        var relative = $"{MigrationsProject(definition)}/Migrations/{migrations[^1]}.cs";
        var path = Path.Combine(serviceDirectory, relative);
        var review = Review(File.ReadAllText(path), migrations[^1], relative, definition);

        if (apply)
        {
            if (review.Changed)
            {
                File.WriteAllText(path, review.Source);
            }

            if (definition.PendingRenames is not null)
            {
                File.WriteAllText(
                    Path.Combine(serviceDirectory, ServiceGenerator.DefinitionFileName),
                    ServiceGenerator.Serialize(definition with { PendingRenames = null }));
            }
        }

        return review;
    }

    /// <summary>A migration with renames was removed: its renames go back to msg.json for the next one.</summary>
    public static void RestoreRenames(string serviceDirectory, IReadOnlyList<PropertyRename> renames)
    {
        ArgumentNullException.ThrowIfNull(renames);
        if (renames.Count == 0)
        {
            return;
        }

        var definition = ServicePlanner.Load(serviceDirectory);
        var pending = (definition.PendingRenames ?? []).ToList();
        foreach (var rename in renames.Where(r => !pending.Contains(r)
                     && definition.Entities.Any(e => e.Name == r.Entity && e.Properties.Any(p => p.Name == r.To))))
        {
            pending.Add(rename);
        }

        File.WriteAllText(
            Path.Combine(serviceDirectory, ServiceGenerator.DefinitionFileName),
            ServiceGenerator.Serialize(definition with { PendingRenames = pending.Count == 0 ? null : pending }));
    }

    public static MigrationReview Review(string source, string name, string path, ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(definition);

        var findings = new List<MigrationFinding>();
        var applied = new List<PropertyRename>();
        var up = Body(source, "Up");
        var down = Body(source, "Down");
        if (up is null || down is null)
        {
            return new MigrationReview(name, path, [new MigrationFinding(FindingLevel.Warning, "No se reconoce el formato de la migración: Stratum no la ha revisado.")], [], false, source);
        }

        var nl = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var upOps = Operations(source, up.Value);
        var downOps = Operations(source, down.Value);
        var edits = new List<(int Start, int End, string Text)>();

        // 1. Renames recorded in msg.json.
        foreach (var rename in definition.PendingRenames ?? [])
        {
            var table = TableOf(definition, rename.Entity);
            if (table is null)
            {
                continue;
            }

            var label = $"{rename.Entity}.{rename.From} → {rename.To}";
            if (upOps.Any(o => o.Method == "RenameColumn" && o.Str("table") == table && o.Str("name") == rename.From && o.Str("newName") == rename.To))
            {
                applied.Add(rename);
                findings.Add(new MigrationFinding(FindingLevel.Info, $"Renombrado {label}: EF Core ya genera RenameColumn, los datos se conservan."));
                continue;
            }

            var upDrop = upOps.FirstOrDefault(o => o.Method == "DropColumn" && o.Str("table") == table && o.Str("name") == rename.From);
            var upAdd = upOps.FirstOrDefault(o => o.Method == "AddColumn" && o.Str("table") == table && o.Str("name") == rename.To);
            var downDrop = downOps.FirstOrDefault(o => o.Method == "DropColumn" && o.Str("table") == table && o.Str("name") == rename.To);
            var downAdd = downOps.FirstOrDefault(o => o.Method == "AddColumn" && o.Str("table") == table && o.Str("name") == rename.From);

            if (upDrop is null || upAdd is null || downDrop is null || downAdd is null)
            {
                findings.Add(new MigrationFinding(FindingLevel.Warning,
                    $"Renombrado {label}: no está en esta migración (¿se creó antes del cambio?). Revisa que la columna no se borre y se cree de nuevo."));
                continue;
            }

            var sameColumn = upAdd.Generic == downAdd.Generic && SameDefinition(upAdd, downAdd);

            // Up: rename the old column (and alter it if the type changed). Down: the opposite.
            edits.Add((upDrop.Start, upDrop.End, Rename(upDrop.Indent, rename.From, table, rename.To, nl)
                + (sameColumn ? string.Empty : nl + nl + Alter(upDrop.Indent, rename.To, table, upAdd, downAdd, nl))));
            edits.Add((upAdd.Start, upAdd.End, string.Empty));
            edits.Add((downDrop.Start, downDrop.End, (sameColumn ? string.Empty : Alter(downDrop.Indent, rename.To, table, downAdd, upAdd, nl) + nl + nl)
                + Rename(downDrop.Indent, rename.To, table, rename.From, nl)));
            edits.Add((downAdd.Start, downAdd.End, string.Empty));
            applied.Add(rename);

            findings.Add(sameColumn
                ? new MigrationFinding(FindingLevel.Info, $"Renombrado {label}: Stratum ha cambiado DropColumn + AddColumn por RenameColumn, los datos se conservan.")
                : new MigrationFinding(FindingLevel.Warning,
                    $"Renombrado {label} con cambio de tipo: Stratum usa RenameColumn + AlterColumn. Los datos se conservan si la base de datos puede convertir los valores; si no, la migración fallará al aplicarse."));
        }

        // 2. Everything else that deletes or may break data (renames already fixed are skipped).
        var fixedStarts = edits.Select(e => e.Start).ToHashSet();
        foreach (var op in upOps.Where(o => !fixedStarts.Contains(o.Start)))
        {
            var table = op.Str("table");
            switch (op.Method)
            {
                case "DropTable":
                    findings.Add(new MigrationFinding(FindingLevel.Danger, $"Borra la tabla {op.Str("name")} y todos sus datos."));
                    break;
                case "DropColumn":
                    findings.Add(new MigrationFinding(FindingLevel.Danger, $"Borra la columna {table}.{op.Str("name")} y sus datos."));
                    break;
                case "AlterColumn":
                    var changes = new List<string>();
                    if (op.Raw("oldClrType") is { } oldClr && oldClr != $"typeof({op.Generic})")
                    {
                        changes.Add($"cambia el tipo ({oldClr[7..^1]} → {op.Generic}): fallará si algún valor no se puede convertir");
                    }

                    if (op.Raw("oldNullable") == "true" && op.Raw("nullable") is null or "false")
                    {
                        changes.Add("pasa a ser obligatoria: fallará si hay filas sin valor, salvo que la migración tenga defaultValue");
                    }

                    if (int.TryParse(op.Raw("maxLength"), out var max) && (op.Raw("oldMaxLength") is null || (int.TryParse(op.Raw("oldMaxLength"), out var oldMax) && max < oldMax)))
                    {
                        changes.Add($"limita la longitud a {max}: fallará si hay textos más largos");
                    }

                    if (changes.Count > 0)
                    {
                        findings.Add(new MigrationFinding(FindingLevel.Warning, $"Columna {table}.{op.Str("name")}: {string.Join("; ", changes)}."));
                    }

                    break;
                case "RenameColumn":
                    if (!applied.Any(r => TableOf(definition, r.Entity) == table && r.From == op.Str("name")))
                    {
                        findings.Add(new MigrationFinding(FindingLevel.Info, $"Renombra la columna {table}.{op.Str("name")} a {op.Str("newName")}."));
                    }

                    break;
                case "RenameTable":
                    findings.Add(new MigrationFinding(FindingLevel.Info, $"Renombra la tabla {op.Str("name")} a {op.Str("newName")}."));
                    break;
            }
        }

        var result = source;
        foreach (var edit in edits.OrderByDescending(e => e.Start))
        {
            var end = edit.Text.Length == 0 ? RemovalEnd(result, edit.End) : edit.End;
            result = string.Concat(result.AsSpan(0, edit.Start), edit.Text, result.AsSpan(end));
        }

        result = BlankLineBeforeBrace().Replace(result, "$1$2$3");
        return new MigrationReview(name, path, findings, applied, edits.Count > 0, edits.Count > 0 ? result : source);
    }

    private static string? TableOf(ServiceDefinition definition, string entityName) =>
        definition.Entities.FirstOrDefault(e => e.Name == entityName) is { } entity
            ? EntityCodeGen.Tokens(entity, definition.Features)["entities_snake"]
            : null;

    private static bool SameDefinition(Operation a, Operation b)
    {
        static Dictionary<string, string> Relevant(Operation o) =>
            o.Args.Where(a => a.Key is not ("name" or "table" or "defaultValue")).ToDictionary(a => a.Key, a => a.Value);

        var (x, y) = (Relevant(a), Relevant(b));
        return x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }

    private static string Rename(string indent, string name, string table, string newName, string nl) =>
        $"{indent}migrationBuilder.RenameColumn({nl}{indent}    name: \"{name}\",{nl}{indent}    table: \"{table}\",{nl}{indent}    newName: \"{newName}\");";

    /// <summary>AlterColumn from the column described by <paramref name="from"/> to the one described by <paramref name="to"/> (both AddColumn calls).</summary>
    private static string Alter(string indent, string name, string table, Operation to, Operation from, string nl)
    {
        var args = new List<string> { $"name: \"{name}\"", $"table: \"{table}\"" };
        args.AddRange(to.Args.Where(a => a.Key is not ("name" or "table" or "defaultValue")).Select(a => $"{a.Key}: {a.Value}"));
        args.Add($"oldClrType: typeof({from.Generic})");
        args.AddRange(from.Args
            .Where(a => a.Key is not ("name" or "table" or "defaultValue" or "schema"))
            .Select(a => $"old{char.ToUpperInvariant(a.Key[0])}{a.Key[1..]}: {a.Value}"));

        return $"{indent}migrationBuilder.AlterColumn<{to.Generic}>({nl}" +
               string.Join("," + nl, args.Select(a => $"{indent}    {a}")) + ");";
    }

    /// <summary>End of a removed statement: its line break and the blank line that separated it from the next one.</summary>
    private static int RemovalEnd(string text, int end)
    {
        var i = AfterLineBreak(text, end);
        var j = i;
        while (j < text.Length && text[j] is ' ' or '\t' or '\r')
        {
            j++;
        }

        return j < text.Length && text[j] == '\n' ? j + 1 : i;
    }

    private static int AfterLineBreak(string text, int index)
    {
        var i = index;
        while (i < text.Length && text[i] is ' ' or '\t' or '\r')
        {
            i++;
        }

        return i < text.Length && text[i] == '\n' ? i + 1 : index;
    }

    /// <summary>A blank line left between the last statement and the closing brace.</summary>
    [GeneratedRegex(@"(;[ \t]*)(\r?\n)(?:[ \t]*\r?\n)+([ \t]*\})")]
    private static partial Regex BlankLineBeforeBrace();

    // ---- A tiny parser for the code EF Core scaffolds -------------------------------------------------

    private sealed record Operation(string Method, string? Generic, int Start, int End, string Indent, IReadOnlyDictionary<string, string> Args)
    {
        public string? Raw(string key) => Args.TryGetValue(key, out var value) ? value : null;

        public string? Str(string key) => Raw(key) is { Length: >= 2 } v && v[0] == '"' && v[^1] == '"' ? v[1..^1] : null;
    }

    /// <summary>Range (inside the braces) of "void Up(MigrationBuilder ...)" or Down.</summary>
    private static (int Start, int End)? Body(string source, string method)
    {
        var match = Regex.Match(source, $@"void\s+{method}\s*\(\s*MigrationBuilder\s+\w+\s*\)\s*\{{");
        if (!match.Success)
        {
            return null;
        }

        var start = match.Index + match.Length;
        var end = Matching(source, start - 1, '{', '}');
        return end < 0 ? null : (start, end);
    }

    private static List<Operation> Operations(string source, (int Start, int End) body)
    {
        var result = new List<Operation>();
        var pattern = new Regex(@"migrationBuilder\.(\w+)\s*(<([^>(]+)>)?\s*\(");
        var i = body.Start;

        while (i < body.End)
        {
            var match = pattern.Match(source, i, body.End - i);
            if (!match.Success)
            {
                break;
            }

            var open = match.Index + match.Length - 1;
            var close = Matching(source, open, '(', ')');
            if (close < 0)
            {
                break;
            }

            var end = close + 1;
            while (end < body.End && char.IsWhiteSpace(source[end]))
            {
                end++;
            }

            if (end < body.End && source[end] == ';')
            {
                end++;
            }

            var lineStart = source.LastIndexOf('\n', match.Index) + 1;
            var indent = source[lineStart..match.Index];
            var statementStart = string.IsNullOrWhiteSpace(indent) ? lineStart : match.Index;
            result.Add(new Operation(
                match.Groups[1].Value,
                match.Groups[3].Success ? match.Groups[3].Value.Trim() : null,
                statementStart,
                end,
                string.IsNullOrWhiteSpace(indent) ? indent : string.Empty,
                Arguments(source[(open + 1)..close])));

            i = end;
        }

        return result;
    }

    private static Dictionary<string, string> Arguments(string text)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in SplitTopLevel(text))
        {
            var match = Regex.Match(part, @"^\s*(\w+)\s*:\s*(.*?)\s*$", RegexOptions.Singleline);
            if (match.Success)
            {
                args[match.Groups[1].Value] = match.Groups[2].Value;
            }
        }

        return args;
    }

    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var current = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                var close = EndOfString(text, i);
                current.Append(text, i, close - i + 1);
                i = close;
                continue;
            }

            depth += c is '(' or '{' or '[' ? 1 : c is ')' or '}' or ']' ? -1 : 0;
            if (c == ',' && depth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        parts.Add(current.ToString());
        return parts;
    }

    /// <summary>Index of the bracket that closes the one at <paramref name="open"/> (strings skipped), or -1.</summary>
    private static int Matching(string text, int open, char opening, char closing)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                i = EndOfString(text, i);
                continue;
            }

            if (c == opening)
            {
                depth++;
            }
            else if (c == closing && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Index of the closing quote of a regular or verbatim string literal starting at <paramref name="quote"/>.</summary>
    private static int EndOfString(string text, int quote)
    {
        var verbatim = quote > 0 && text[quote - 1] == '@';
        for (var i = quote + 1; i < text.Length; i++)
        {
            if (!verbatim && text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == '"')
            {
                if (verbatim && i + 1 < text.Length && text[i + 1] == '"')
                {
                    i++;
                    continue;
                }

                return i;
            }
        }

        return text.Length - 1;
    }
}
