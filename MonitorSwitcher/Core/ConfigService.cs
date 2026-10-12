using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace MonitorSwitcher.Core;

[DataContract]
public class MonitorConfig
{
    /// <summary>
    /// Stable monitor identity (Windows' monitor device path). Unlike <see cref="DeviceName"/>
    /// it doesn't change when Windows renumbers displays. Missing in configs saved by older versions.
    /// </summary>
    [DataMember(Name = "monitorId", Order = 0, EmitDefaultValue = false)]
    public string? MonitorId { get; set; }

    [DataMember(Name = "deviceName", Order = 1)]
    public string DeviceName { get; set; } = string.Empty;

    [DataMember(Name = "displayName", Order = 2)]
    public string DisplayName { get; set; } = string.Empty;

    [DataMember(Name = "inputs", Order = 3)]
    public List<InputConfig> Inputs { get; set; } = new();

    [DataMember(Name = "isDisabled", Order = 4)]
    public bool IsDisabled { get; set; }

    /// <summary>VCP values the user removed, so automatic detection doesn't add them back.</summary>
    [DataMember(Name = "removedInputs", Order = 5, EmitDefaultValue = false)]
    public List<uint>? RemovedInputs { get; set; }
}

[DataContract]
public class InputConfig
{
    [DataMember(Name = "label", Order = 0)]
    public string Label { get; set; } = string.Empty;

    [DataMember(Name = "vcpValue", Order = 1)]
    public uint VcpValue { get; set; }
}

/// <summary>A named set of inputs to switch several monitors to with one click.</summary>
[DataContract]
public class PresetConfig
{
    [DataMember(Name = "name", Order = 0)]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "targets", Order = 1)]
    public List<PresetTargetConfig> Targets { get; set; } = new();
}

[DataContract]
public class PresetTargetConfig
{
    [DataMember(Name = "monitorId", Order = 0, EmitDefaultValue = false)]
    public string? MonitorId { get; set; }

    [DataMember(Name = "deviceName", Order = 1)]
    public string DeviceName { get; set; } = string.Empty;

    [DataMember(Name = "vcpValue", Order = 2)]
    public uint VcpValue { get; set; }
}

[DataContract]
public class AppConfig
{
    [DataMember(Name = "alwaysOnTop", Order = 0)]
    public bool AlwaysOnTop { get; set; }

    [DataMember(Name = "monitors", Order = 1)]
    public List<MonitorConfig> Monitors { get; set; } = new();

    [DataMember(Name = "presets", Order = 2)]
    public List<PresetConfig> Presets { get; set; } = new();
}

public static class ConfigService
{
    private static readonly object Gate = new();

    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MonitorSwitcher");

    public static string ConfigFilePath { get; } = Path.Combine(ConfigDirectory, "config.json");

    private static readonly string LegacyConfigPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

    public static bool ConfigExists() => File.Exists(ConfigFilePath) || File.Exists(LegacyConfigPath);

    public static AppConfig Load()
    {
        lock (Gate)
        {
            try
            {
                MigrateLegacyConfig();

                if (!File.Exists(ConfigFilePath))
                    return new AppConfig();

                using var stream = new MemoryStream(File.ReadAllBytes(ConfigFilePath));
                var config = (AppConfig?)CreateSerializer().ReadObject(stream);
                return Normalize(config ?? new AppConfig());
            }
            catch (SerializationException ex)
            {
                // Keep the unreadable file for the user instead of silently overwriting it later.
                Log.Error($"{ConfigFilePath} is not valid JSON, starting with an empty config", ex);
                TryBackUpInvalidConfig();
                return new AppConfig();
            }
            catch (Exception ex)
            {
                Log.Error($"Could not read {ConfigFilePath}", ex);
                return new AppConfig();
            }
        }
    }

    /// <summary>Saves the config; returns false and a user-facing reason on failure.</summary>
    public static bool TrySave(AppConfig config, out string? error)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(ConfigDirectory);

                // Write to a temp file first so a failure mid-write can't corrupt the config.
                string tempPath = ConfigFilePath + ".tmp";
                using (var stream = File.Create(tempPath))
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(
                           stream, new UTF8Encoding(false), ownsStream: false, indent: true, indentChars: "  "))
                {
                    CreateSerializer().WriteObject(writer, config);
                    writer.Flush();
                }

                ReplaceWithRetry(tempPath, ConfigFilePath);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not save {ConfigFilePath}", ex);
                error = ex.Message;
                return false;
            }
        }
    }

    private static DataContractJsonSerializer CreateSerializer() => new(typeof(AppConfig));

    private static void ReplaceWithRetry(string source, string destination)
    {
        // Antivirus or sync tools can briefly lock the file; give them a moment.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                File.Delete(source);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(150 * attempt);
            }
        }
    }

    private static void MigrateLegacyConfig()
    {
        if (File.Exists(ConfigFilePath) || !File.Exists(LegacyConfigPath))
            return;

        Directory.CreateDirectory(ConfigDirectory);
        File.Copy(LegacyConfigPath, ConfigFilePath);
        try { File.Delete(LegacyConfigPath); } catch { }
        Log.Info($"Migrated {LegacyConfigPath} to {ConfigFilePath}");
    }

    private static void TryBackUpInvalidConfig()
    {
        try
        {
            File.Copy(ConfigFilePath, Path.Combine(ConfigDirectory, "config.invalid.json"), overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not back up the invalid config", ex);
        }
    }

    // The serializer doesn't run constructors or initializers, so fill in anything the file left out.
    private static AppConfig Normalize(AppConfig config)
    {
        config.Monitors ??= new List<MonitorConfig>();
        config.Monitors.RemoveAll(m => m == null);
        foreach (var monitor in config.Monitors)
        {
            monitor.DeviceName ??= string.Empty;
            monitor.DisplayName ??= string.Empty;
            monitor.Inputs ??= new List<InputConfig>();
            monitor.Inputs.RemoveAll(i => i == null);
            foreach (var input in monitor.Inputs)
                input.Label ??= string.Empty;
        }

        config.Presets ??= new List<PresetConfig>();
        config.Presets.RemoveAll(p => p == null);
        foreach (var preset in config.Presets)
        {
            preset.Name ??= string.Empty;
            preset.Targets ??= new List<PresetTargetConfig>();
            preset.Targets.RemoveAll(t => t == null);
            foreach (var target in preset.Targets)
                target.DeviceName ??= string.Empty;
        }
        return config;
    }
}
