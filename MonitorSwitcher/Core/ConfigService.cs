using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorSwitcher.Core;

public class MonitorConfig
{
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("inputs")]
    public List<InputConfig> Inputs { get; set; } = new();
}

public class InputConfig
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("vcpValue")]
    public uint VcpValue { get; set; }
}

public class AppConfig
{
    [JsonPropertyName("monitors")]
    public List<MonitorConfig> Monitors { get; set; } = new();
}

public static class ConfigService
{
    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static List<MonitorConfig> LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new List<MonitorConfig>();

            string json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            return config?.Monitors ?? new List<MonitorConfig>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"ConfigService.LoadConfig error: {ex.Message}");
            return new List<MonitorConfig>();
        }
    }

    public static void SaveConfig(List<MonitorConfig> configs)
    {
        try
        {
            var appConfig = new AppConfig { Monitors = configs };
            string json = JsonSerializer.Serialize(appConfig, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"ConfigService.SaveConfig error: {ex.Message}");
        }
    }

    public static bool ConfigExists() => File.Exists(ConfigPath);
}
