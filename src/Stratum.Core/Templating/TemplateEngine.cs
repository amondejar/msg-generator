using System.Text;
using System.Text.RegularExpressions;

namespace Stratum.Core.Templating;

/// <summary>
/// Deliberately tiny template engine:
/// <list type="bullet">
/// <item>Tokens: <c>{{Service}}</c>, <c>{{service-kebab}}</c>... Only exact known keys are replaced,
/// so things like GitHub Actions' <c>${{ github.sha }}</c> are left untouched.</item>
/// <item>Line directives: any line containing <c>@stratum:if feature</c>, <c>@stratum:if !feature</c>,
/// <c>@stratum:else</c> or <c>@stratum:end</c> is removed from the output and controls the lines between them.
/// The directive can live inside whatever comment syntax the file uses (<c>//</c>, <c>#</c>, <c>&lt;!-- --&gt;</c>),
/// so templates stay valid C#, XML, YAML or JSON-ish and are pleasant to edit.</item>
/// </list>
/// </summary>
public static partial class TemplateEngine
{
    [GeneratedRegex(@"@(?:stratum|msg):(?<kind>if|else|end)\b(?:\s+(?<neg>!)?(?<feature>[A-Za-z][A-Za-z0-9]*))?")]
    private static partial Regex Directive();

    public static string Render(
        string template,
        IReadOnlyDictionary<string, string> tokens,
        Func<string, bool> isEnabled,
        string templateName = "<template>")
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(isEnabled);

        var newline = template.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = template.Split('\n');
        var output = new StringBuilder(template.Length);

        // Each frame: was the parent block active, and is this branch's condition true.
        var stack = new Stack<(bool ParentActive, bool Condition, bool InElse, int Line)>();
        var active = true;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var match = Directive().Match(line);

            if (match.Success)
            {
                switch (match.Groups["kind"].Value)
                {
                    case "if":
                        if (!match.Groups["feature"].Success)
                        {
                            throw Error(templateName, i, "'@stratum:if' necesita una feature.");
                        }

                        bool value;
                        try
                        {
                            value = isEnabled(match.Groups["feature"].Value);
                        }
                        catch (StratumException ex)
                        {
                            throw Error(templateName, i, ex.Message);
                        }

                        if (match.Groups["neg"].Success)
                        {
                            value = !value;
                        }

                        stack.Push((active, value, false, i));
                        active = active && value;
                        break;

                    case "else":
                        if (stack.Count == 0)
                        {
                            throw Error(templateName, i, "'@stratum:else' sin '@stratum:if'.");
                        }

                        var frame = stack.Pop();
                        if (frame.InElse)
                        {
                            throw Error(templateName, i, "'@stratum:else' duplicado.");
                        }

                        stack.Push(frame with { InElse = true });
                        active = frame.ParentActive && !frame.Condition;
                        break;

                    default: // end
                        if (stack.Count == 0)
                        {
                            throw Error(templateName, i, "'@stratum:end' sin '@stratum:if'.");
                        }

                        active = stack.Pop().ParentActive;
                        break;
                }

                continue;
            }

            if (!active)
            {
                continue;
            }

            output.Append(ReplaceTokens(line, tokens));
            if (i < lines.Length - 1)
            {
                output.Append(newline);
            }
        }

        if (stack.Count > 0)
        {
            throw Error(templateName, stack.Peek().Line, "'@stratum:if' sin cerrar con '@stratum:end'.");
        }

        return output.ToString();
    }

    public static string ReplaceTokens(string text, IReadOnlyDictionary<string, string> tokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(tokens);

        if (!text.Contains("{{", StringComparison.Ordinal))
        {
            return text;
        }

        foreach (var (key, value) in tokens)
        {
            text = text.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
        }

        return text;
    }

    private static StratumException Error(string template, int lineIndex, string message) =>
        new($"Error en plantilla {template}, línea {lineIndex + 1}: {message}");
}
