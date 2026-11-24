using System.Text;
using System.Text.Json;

namespace Stratum.Core.Generation;

/// <summary>How to register "stratum mcp" in AI assistants (Claude Code, Claude Desktop, VS Code, Cursor).</summary>
public static class McpClientConfig
{
    /// <param name="root">Folder of the system the MCP server works on.</param>
    public static string Text(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        // Installed as a dotnet tool the process is "stratum"; run from source it is "dotnet stratum.dll".
        var process = Environment.ProcessPath ?? "dotnet";
        var installed = !Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var command = installed ? process : "dotnet";
        string[] commandArgs = installed ? ["mcp", "--path", root] : [Path.Combine(AppContext.BaseDirectory, "stratum.dll"), "mcp", "--path", root];
        var json = new JsonSerializerOptions { WriteIndented = true };
        static string Quote(string value) => JsonSerializer.Serialize(value);

        var output = new StringBuilder();
        output.AppendLine("Claude Code (en la carpeta del sistema):");
        output.AppendLine($"  claude mcp add stratum -- {(installed ? Quote(command) : "dotnet")} {string.Join(' ', commandArgs.Select(a => a.Contains(' ', StringComparison.Ordinal) ? Quote(a) : a))}");
        output.AppendLine();
        output.AppendLine("Claude Desktop (claude_desktop_config.json, dentro de \"mcpServers\"):");
        output.AppendLine("  \"stratum\": " + JsonSerializer.Serialize(new { command, args = commandArgs }, json).Replace("\n", "\n  ", StringComparison.Ordinal));
        output.AppendLine();
        output.AppendLine("VS Code / GitHub Copilot (.vscode/mcp.json, dentro de \"servers\"):");
        output.AppendLine("  \"stratum\": " + JsonSerializer.Serialize(new { type = "stdio", command, args = commandArgs }, json).Replace("\n", "\n  ", StringComparison.Ordinal));
        output.AppendLine();
        output.AppendLine("Cursor (.cursor/mcp.json, dentro de \"mcpServers\"): igual que Claude Desktop.");
        output.AppendLine();
        output.AppendLine("Las herramientas que cambian algo devuelven primero un plan; el asistente aplica con \"apply\": true cuando tú lo apruebas.");
        return output.ToString();
    }
}
