using System.Text.Json;

namespace LeiGodAutoPause;

public sealed class AppConfig
{
    public bool PauseWhenAllGamesExit { get; set; } = true;

    public bool ResumeWhenGameStarts { get; set; }

    public bool PauseOnShutdown { get; set; } = true;

    public bool StartWithWindows { get; set; } = true;

    public int ExitDelaySeconds { get; set; } = 15;

    public int PollSeconds { get; set; } = 3;

    public bool CloseToTray { get; set; } = true;

    public List<string> GamePaths { get; set; } = new();

    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LeiGodAutoPause");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return new AppConfig();
            }

            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json);
            return config ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDirectory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}

