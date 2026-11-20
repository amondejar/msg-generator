using System.Text;
using System.Text.RegularExpressions;

namespace Stratum.Core.Model;

/// <summary>Normalizes and validates service names ("order-service" → "OrderService").</summary>
public static partial class ServiceName
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Microsoft", "Program", "Msg", "Stratum", "Api", "Tests", "ServiceDefaults",
    };

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$")]
    private static partial Regex PascalCase();

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex Separators();

    [GeneratedRegex("(?<=[a-z0-9])([A-Z])")]
    private static partial Regex WordBoundary();

    /// <summary>Turns user input into a valid PascalCase identifier or throws with a helpful message.</summary>
    public static string Normalize(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var parts = Separators().Split(input.Trim()).Where(p => p.Length > 0);
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            sb.Append(char.ToUpperInvariant(part[0])).Append(part.AsSpan(1));
        }

        var name = sb.ToString();
        Validate(name);
        return name;
    }

    public static void Validate(string name)
    {
        if (string.IsNullOrEmpty(name) || !PascalCase().IsMatch(name))
        {
            throw new StratumException(
                $"Nombre de servicio no válido: '{name}'. Debe empezar por letra y contener solo letras y números (p. ej. Orders, OrderService).");
        }

        if (name.Length > 50)
        {
            throw new StratumException($"El nombre '{name}' es demasiado largo (máximo 50 caracteres).");
        }

        if (Reserved.Contains(name))
        {
            throw new StratumException($"'{name}' es un nombre reservado. Elige otro (p. ej. {name}Service).");
        }
    }

    /// <summary>"OrderService" → "order-service". Used for container images, OTel service.name, config values.</summary>
    public static string ToKebab(string pascal) =>
        WordBoundary().Replace(pascal, "-$1").ToLowerInvariant();

    /// <summary>Deterministic local dev port per service (5100-5999) so several services can run side by side.</summary>
    public static int DevPort(string pascal)
    {
        // FNV-1a: stable across runs and platforms (string.GetHashCode is randomized).
        uint hash = 2166136261;
        foreach (var c in pascal.ToUpperInvariant())
        {
            hash = (hash ^ c) * 16777619;
        }

        return 5100 + (int)(hash % 900);
    }
}
