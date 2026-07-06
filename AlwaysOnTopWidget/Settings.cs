using System.Text.Json;

namespace AlwaysOnTopWidget;

public class Settings
{
    // int.MinValue = "no saved position yet" -> caller falls back to the default corner.
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = int.MinValue;
    public bool TopMost { get; set; } = true;
    public int PollSeconds { get; set; } = 60;

    static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (loaded != null) return loaded;
            }
        }
        catch { /* missing/corrupt settings file -> fall back to defaults below */ }
        return new Settings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch { /* best-effort; a failed save just means next launch uses the last-good settings */ }
    }
}
