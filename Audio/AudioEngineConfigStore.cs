using System.IO;
using System.Text.Json;

namespace BassRouter.Audio;

public sealed class AudioEngineConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string ConfigPath;

    public AudioEngineConfigStore()
    {
        // %LOCALAPPDATA% on Windows, $XDG_CONFIG_HOME (~/.config) on Linux
        Environment.SpecialFolder baseFolder = OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.LocalApplicationData
            : Environment.SpecialFolder.ApplicationData;

        string appDataDirectory = Path.Combine(
            Environment.GetFolderPath(baseFolder),
            "SamsidParty",
            "BassRouter");

        ConfigPath = Path.Combine(appDataDirectory, "config.json");
    }

    public AudioEngineConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return AudioEngineConfig.Default;

            string json = File.ReadAllText(ConfigPath);
            AudioEngineConfig? config = JsonSerializer.Deserialize<AudioEngineConfig>(json, JsonOptions);

            return config ?? AudioEngineConfig.Default;
        }
        catch
        {
            return AudioEngineConfig.Default;
        }
    }

    public void Save(AudioEngineConfig config)
    {
        string? directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }
}