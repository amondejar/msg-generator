using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Msg.Core.Import;

/// <summary>
/// YAML → JSON for the subset API specifications use: block mappings and sequences, plain/quoted/block scalars,
/// flow collections and comments. No anchors, tags or multiple documents (an error says so). Kept in Msg.Core
/// so Stratum has no third-party dependencies.
/// </summary>
public static class MiniYaml
{
    public static JsonNode? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<Line>();
        var raw = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < raw.Length; i++)
        {
            var line = raw[i].TrimEnd();
            if (line.TrimStart().StartsWith('#') || line.Trim().Length == 0 || line == "---" || line.StartsWith('%'))
            {
                lines.Add(new Line(i + 1, -1, string.Empty, raw[i]));
                continue;
            }

            if (line == "...")
            {
                break;
            }

            var indent = line.Length - line.TrimStart(' ').Length;
            if (line.Length > indent && line[indent] == '\t')
            {
                throw Error(i + 1, "tabulador como sangría");
            }

            lines.Add(new Line(i + 1, indent, StripComment(line[indent..]), raw[i]));
        }

        var parser = new Parser(lines);
        var result = parser.ParseBlock(0, -1);
        parser.ExpectEnd();
        return result;
    }

    private sealed record Line(int Number, int Indent, string Content, string Raw)
    {
        public bool Blank => Indent < 0;
    }

    private sealed class Parser(List<Line> lines)
    {
        private int _pos;

        public void ExpectEnd()
        {
            SkipBlank();
            if (_pos < lines.Count)
            {
                throw Error(lines[_pos].Number, $"contenido inesperado '{lines[_pos].Content}'");
            }
        }

        private void SkipBlank()
        {
            while (_pos < lines.Count && lines[_pos].Blank)
            {
                _pos++;
            }
        }

        /// <summary>The node that starts at the next non-blank line, if it is indented more than <paramref name="parentIndent"/>.</summary>
        public JsonNode? ParseBlock(int minIndent, int parentIndent)
        {
            SkipBlank();
            if (_pos >= lines.Count || lines[_pos].Indent <= parentIndent || lines[_pos].Indent < minIndent)
            {
                return null;
            }

            var line = lines[_pos];
            return IsSequenceItem(line.Content) ? ParseSequence(line.Indent) : ParseMappingOrScalar(line.Indent);
        }

        private static bool IsSequenceItem(string content) => content == "-" || content.StartsWith("- ", StringComparison.Ordinal);

        private JsonArray ParseSequence(int indent)
        {
            var array = new JsonArray();
            while (true)
            {
                SkipBlank();
                if (_pos >= lines.Count || lines[_pos].Indent != indent || !IsSequenceItem(lines[_pos].Content))
                {
                    break;
                }

                var line = lines[_pos];
                var rest = line.Content.Length > 1 ? line.Content[2..].TrimStart() : string.Empty;
                if (rest.Length == 0)
                {
                    _pos++;
                    array.Add(ParseBlock(indent + 1, indent));
                    continue;
                }

                // "- key: value" starts a mapping whose keys are aligned with "key".
                var itemIndent = indent + (line.Content.Length - rest.Length);
                lines[_pos] = line with { Indent = itemIndent, Content = rest };
                array.Add(IsSequenceItem(rest) ? ParseSequence(itemIndent) : ParseMappingOrScalar(itemIndent));
            }

            return array;
        }

        private JsonNode? ParseMappingOrScalar(int indent)
        {
            var line = lines[_pos];
            if (FindColon(line.Content) < 0)
            {
                _pos++;
                return ParseScalarValue(line.Content, line, indent - 1);
            }

            var obj = new JsonObject();
            while (true)
            {
                SkipBlank();
                if (_pos >= lines.Count || lines[_pos].Indent != indent)
                {
                    if (_pos < lines.Count && lines[_pos].Indent > indent)
                    {
                        throw Error(lines[_pos].Number, "sangría inesperada");
                    }

                    break;
                }

                line = lines[_pos];
                if (IsSequenceItem(line.Content))
                {
                    break;
                }

                var colon = FindColon(line.Content);
                if (colon < 0)
                {
                    throw Error(line.Number, $"se esperaba 'clave: valor' en '{line.Content}'");
                }

                var key = Unquote(line.Content[..colon].Trim(), line.Number);
                var value = line.Content[(colon + 1)..].Trim();
                _pos++;
                if (value.Length == 0)
                {
                    // Nested block; a sequence may sit at the same indentation as its key.
                    SkipBlank();
                    obj[key] = _pos < lines.Count && lines[_pos].Indent == indent && IsSequenceItem(lines[_pos].Content)
                        ? ParseSequence(indent)
                        : ParseBlock(indent + 1, indent);
                }
                else
                {
                    obj[key] = ParseScalarValue(value, line, indent);
                }
            }

            return obj;
        }

        private JsonNode? ParseScalarValue(string value, Line line, int indent)
        {
            if (value.StartsWith('&') || value.StartsWith('*') || value.StartsWith("!!", StringComparison.Ordinal))
            {
                throw Error(line.Number, "anclas, alias y etiquetas YAML no están soportados: convierte la especificación a JSON");
            }

            if (value[0] is '|' or '>')
            {
                return BlockScalar(value, indent);
            }

            if (value[0] is '[' or '{')
            {
                var text = value;
                while (!Balanced(text) && _pos < lines.Count)
                {
                    text += " " + lines[_pos].Raw.Trim();
                    _pos++;
                }

                var index = 0;
                return Flow(text, ref index, line.Number);
            }

            if (value[0] is '"' or '\'')
            {
                var text = value;
                while (!ClosedQuote(text) && _pos < lines.Count)
                {
                    text += " " + lines[_pos].Raw.Trim();
                    _pos++;
                }

                return JsonValue.Create(Unquote(text, line.Number));
            }

            // Plain scalar continued on more-indented lines (folded with spaces).
            var plain = new StringBuilder(value);
            while (_pos < lines.Count && !lines[_pos].Blank && lines[_pos].Indent > indent && FindColon(lines[_pos].Content) < 0 && !IsSequenceItem(lines[_pos].Content))
            {
                plain.Append(' ').Append(lines[_pos].Content.Trim());
                _pos++;
            }

            return Typed(plain.ToString());
        }

        private JsonValue BlockScalar(string header, int indent)
        {
            var folded = header[0] == '>';
            var chomp = header.Contains('-', StringComparison.Ordinal) ? '-' : header.Contains('+', StringComparison.Ordinal) ? '+' : ' ';
            var collected = new List<string>();
            var blockIndent = -1;
            while (_pos < lines.Count)
            {
                var line = lines[_pos];
                var rawIndent = line.Raw.Length - line.Raw.TrimStart(' ').Length;
                if (line.Raw.Trim().Length > 0 && rawIndent <= indent)
                {
                    break;
                }

                if (line.Raw.Trim().Length > 0 && blockIndent < 0)
                {
                    blockIndent = rawIndent;
                }

                collected.Add(line.Raw.Trim().Length == 0 ? string.Empty : line.Raw[Math.Min(blockIndent, rawIndent)..].TrimEnd('\r'));
                _pos++;
            }

            var trailing = 0;
            while (collected.Count > 0 && collected[^1].Length == 0)
            {
                collected.RemoveAt(collected.Count - 1);
                trailing++;
            }

            string text;
            if (folded)
            {
                var sb = new StringBuilder();
                for (var i = 0; i < collected.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(collected[i].Length == 0 || collected[i - 1].Length == 0 || collected[i].StartsWith(' ') ? "\n" : " ");
                    }

                    sb.Append(collected[i]);
                }

                text = sb.ToString();
            }
            else
            {
                text = string.Join("\n", collected);
            }

            text = chomp switch
            {
                '-' => text,
                '+' => text + new string('\n', trailing + 1),
                _ => collected.Count == 0 ? string.Empty : text + "\n",
            };
            return JsonValue.Create(text);
        }
    }

    private static JsonNode? Flow(string text, ref int i, int line)
    {
        SkipSpaces(text, ref i);
        if (i >= text.Length)
        {
            throw Error(line, "colección sin cerrar");
        }

        if (text[i] == '[')
        {
            i++;
            var array = new JsonArray();
            SkipSpaces(text, ref i);
            if (i < text.Length && text[i] == ']')
            {
                i++;
                return array;
            }

            while (true)
            {
                array.Add(Flow(text, ref i, line));
                SkipSpaces(text, ref i);
                if (i < text.Length && text[i] == ',')
                {
                    i++;
                    SkipSpaces(text, ref i);
                    if (i < text.Length && text[i] == ']')
                    {
                        i++;
                        return array;   // trailing comma
                    }

                    continue;
                }

                if (i < text.Length && text[i] == ']')
                {
                    i++;
                    return array;
                }

                throw Error(line, "se esperaba ',' o ']'");
            }
        }

        if (text[i] == '{')
        {
            i++;
            var obj = new JsonObject();
            SkipSpaces(text, ref i);
            if (i < text.Length && text[i] == '}')
            {
                i++;
                return obj;
            }

            while (true)
            {
                var key = FlowScalar(text, ref i, line, ":,}");
                SkipSpaces(text, ref i);
                JsonNode? value = null;
                if (i < text.Length && text[i] == ':')
                {
                    i++;
                    value = Flow(text, ref i, line);
                }

                obj[key?.ToString() ?? "null"] = value;
                SkipSpaces(text, ref i);
                if (i < text.Length && text[i] == ',')
                {
                    i++;
                    SkipSpaces(text, ref i);
                    if (i < text.Length && text[i] == '}')
                    {
                        i++;
                        return obj;   // trailing comma
                    }

                    continue;
                }

                if (i < text.Length && text[i] == '}')
                {
                    i++;
                    return obj;
                }

                throw Error(line, "se esperaba ',' o '}'");
            }
        }

        return FlowScalar(text, ref i, line, ",]}");
    }

    private static JsonNode? FlowScalar(string text, ref int i, int line, string stops)
    {
        SkipSpaces(text, ref i);
        if (i < text.Length && text[i] is '"' or '\'')
        {
            var quote = text[i];
            var start = i;
            i++;
            while (i < text.Length)
            {
                if (text[i] == '\\' && quote == '"')
                {
                    i += 2;
                    continue;
                }

                if (text[i] == quote)
                {
                    if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        i += 2;
                        continue;
                    }

                    i++;
                    break;
                }

                i++;
            }

            return JsonValue.Create(Unquote(text[start..i], line));
        }

        if (i < text.Length && text[i] is '[' or '{')
        {
            return Flow(text, ref i, line);
        }

        var begin = i;
        while (i < text.Length && !stops.Contains(text[i], StringComparison.Ordinal)
               && !(text[i] == ':' && (i + 1 >= text.Length || text[i + 1] == ' ')))
        {
            i++;
        }

        return Typed(text[begin..i].Trim());
    }

    private static void SkipSpaces(string text, ref int i)
    {
        while (i < text.Length && text[i] == ' ')
        {
            i++;
        }
    }

    private static JsonNode? Typed(string value)
    {
        switch (value)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }

        var digits = value.TrimStart('-', '+');
        if (digits.Length > 0 && char.IsDigit(digits[0]))
        {
            if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) && !(digits.Length > 1 && digits[0] == '0'))
            {
                return JsonValue.Create(integer);
            }

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && !(digits.Length > 1 && digits[0] == '0' && digits[1] != '.'))
            {
                return JsonValue.Create(number);
            }
        }

        return JsonValue.Create(value);
    }

    private static string Unquote(string text, int line)
    {
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            return text[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            var sb = new StringBuilder();
            for (var i = 1; i < text.Length - 1; i++)
            {
                if (text[i] != '\\')
                {
                    sb.Append(text[i]);
                    continue;
                }

                i++;
                switch (text[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case '0': sb.Append('\0'); break;
                    case ' ': sb.Append(' '); break;
                    case 'u':
                        sb.Append((char)int.Parse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    case 'U':
                        sb.Append(char.ConvertFromUtf32(int.Parse(text.AsSpan(i + 1, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                        i += 8;
                        break;
                    case 'x':
                        sb.Append((char)int.Parse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 2;
                        break;
                    default: throw Error(line, $"escape '\\{text[i]}' no soportado");
                }
            }

            return sb.ToString();
        }

        return text;
    }

    /// <summary>Index of the "key: value" colon outside quotes and brackets, or -1.</summary>
    private static int FindColon(string content)
    {
        if (content.Length == 0 || content[0] is '[' or '{')
        {
            return -1;
        }

        var quote = '\0';
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (quote != '\0')
            {
                if (c == quote && !(quote == '"' && content[i - 1] == '\\'))
                {
                    quote = '\0';
                }

                continue;
            }

            if ((c == '"' || c == '\'') && i == 0)
            {
                quote = c;
                continue;
            }

            if (c == ':' && (i + 1 == content.Length || content[i + 1] == ' '))
            {
                return i;
            }
        }

        return -1;
    }

    private static string StripComment(string content)
    {
        var quote = '\0';
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if ((c == '"' || c == '\'') && (i == 0 || content[i - 1] is ' ' or ':' or '[' or '{' or ',' or '-'))
            {
                quote = c;
            }
            else if (c == '#' && i > 0 && content[i - 1] == ' ')
            {
                return content[..i].TrimEnd();
            }
        }

        return content;
    }

    private static bool Balanced(string text)
    {
        var depth = 0;
        var quote = '\0';
        foreach (var c in text)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '[' or '{':
                    depth++;
                    break;
                case ']' or '}':
                    depth--;
                    break;
            }
        }

        return depth <= 0;
    }

    private static bool ClosedQuote(string text) =>
        text.Length >= 2 && text[^1] == text[0] && !(text[0] == '"' && text[^2] == '\\');

    private static MsgException Error(int line, string message) => new($"YAML línea {line}: {message}.");
}
