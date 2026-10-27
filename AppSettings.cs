using System.IO;
using System.Text.Json;

namespace MultiSpeaker;

sealed class SpeakerSettings
{
    public bool Enabled { get; set; }
    public int DelayMs { get; set; }
}

/// <summary>Remembers the chosen source and per-speaker delays in %APPDATA%\MultiSpeaker.</summary>
sealed class AppSettings
{
    public bool? DarkMode { get; set; }  // null = follow Windows
    public bool LinkVolumes { get; set; }
    public Dictionary<string, SpeakerSettings> Speakers { get; set; } = [];

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiSpeaker", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Unreadable settings: fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Losing settings is not worth crashing over.
        }
    }
}
