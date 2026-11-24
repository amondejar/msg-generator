using System.Text.Json;

namespace Stratum.Studio.State;

/// <summary>The user's Studio preferences (editor, language), in %APPDATA%/Stratum/studio.json.</summary>
public static class StudioSettings
{
    private static readonly string File = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stratum", "studio.json");

    private static readonly Lock Gate = new();

    public static string? Get(string key)
    {
        lock (Gate)
        {
            return Read().GetValueOrDefault(key);
        }
    }

    public static void Set(string key, string value)
    {
        lock (Gate)
        {
            var settings = Read();
            settings[key] = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                System.IO.File.WriteAllText(File, JsonSerializer.Serialize(settings));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // not remembered; it still works for this session
            }
        }
    }

    private static Dictionary<string, string> Read()
    {
        try
        {
            return System.IO.File.Exists(File)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(File)) ?? []
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
