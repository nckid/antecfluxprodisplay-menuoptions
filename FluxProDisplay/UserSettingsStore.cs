using System.Text.Json;

namespace FluxProDisplay;

/// <summary>
/// Persists user preferences to %APPDATA%\FluxProDisplay so they survive app restarts,
/// rebuilds, re-installs, and system reboots regardless of where the executable lives.
/// </summary>
internal static class UserSettingsStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FluxProDisplay");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    private sealed class UserSettings
    {
        public string? SelectedCpuSensor { get; set; }
        public string? SelectedGpuSensor { get; set; }
    }

    public static (string? CpuSensor, string? GpuSensor) Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return (null, null);

            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath));
            return (settings?.SelectedCpuSensor, settings?.SelectedGpuSensor);
        }
        catch
        {
            // A corrupt settings file must never prevent the app from starting.
            return (null, null);
        }
    }

    public static void Save(string? cpuSensor, string? gpuSensor)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);

            var settings = new UserSettings
            {
                SelectedCpuSensor = cpuSensor,
                SelectedGpuSensor = gpuSensor
            };

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Logger.LogError(new Exception("Failed to save user settings", ex));
        }
    }
}
