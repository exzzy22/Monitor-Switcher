using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using MonitorSwitcher.Core;
using MonitorSwitcher.ViewModels;

namespace MonitorSwitcher.Views;

public partial class ConfigWindow : Window
{
    private readonly List<ConfigMonitorEntry> _entries;
    private readonly ObservableCollection<ConfigPresetEntry> _presets;

    public ConfigWindow(List<MonitorViewModel> monitors)
    {
        InitializeComponent();

        _entries = monitors.Select(m => new ConfigMonitorEntry
        {
            Id = m.Id,
            DeviceName = m.DeviceName,
            DisplayName = m.DisplayName,
            IsConnected = m.IsConnected,
            MonitorInfo = m.MonitorInfo,
            IsDisabled = m.MonitorInfo.IsDisabled,
            Inputs = new ObservableCollection<ConfigInputEntry>(
                m.MonitorInfo.Inputs.Select(i => new ConfigInputEntry(i.Label, i.VcpValue))),
            RemovedInputs = m.MonitorInfo.RemovedInputs.ToList(),
        }).ToList();

        _presets = new ObservableCollection<ConfigPresetEntry>(
            ConfigService.Load().Presets.Select(p => new ConfigPresetEntry(p.Name, _entries, p)));

        MonitorList.ItemsSource = _entries;
        PresetList.ItemsSource = _presets;
        ConfigPathText.Text = $"Config: {ConfigService.ConfigFilePath}";
    }

    /// <summary>The config that was saved, when the dialog closes with Save.</summary>
    public AppConfig? SavedConfig { get; private set; }

    private void AddInputCombo_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox combo)
        {
            combo.ItemsSource = InputPreset.All;
        }
    }

    private void AddInputPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Parent is StackPanel sp)
        {
            var combo = sp.Children.OfType<ComboBox>().FirstOrDefault();
            if (combo?.SelectedItem is InputPreset preset
                && combo.Tag is ConfigMonitorEntry monitorEntry)
            {
                if (monitorEntry.Inputs.Any(i => i.VcpValue == preset.VcpValue))
                {
                    MessageBox.Show($"\"{preset.DisplayText}\" is already added.",
                        "Duplicate", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                monitorEntry.AddInput(preset.Label, preset.VcpValue);
                combo.SelectedItem = null;
            }
            else
            {
                MessageBox.Show("Select an input from the dropdown first.",
                    "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private void AddManual_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Parent is StackPanel sp)
        {
            var textBoxes = sp.Children.OfType<TextBox>().ToList();
            if (textBoxes.Count < 2) return;

            var labelBox = textBoxes[0];
            var vcpBox = textBoxes[1];
            var monitorEntry = sp.Tag as ConfigMonitorEntry;

            if (monitorEntry == null)
                return;

            string label = labelBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(label))
            {
                MessageBox.Show("Enter a label for the input.",
                    "Missing Label", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!TryParseVcpValue(vcpBox.Text, out uint vcpValue))
            {
                MessageBox.Show("VCP value must be a number between 0 and 255 (hex like 0x0F also works).",
                    "Invalid Value", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (monitorEntry.Inputs.Any(i => i.VcpValue == vcpValue))
            {
                MessageBox.Show($"VCP {vcpValue} is already added.",
                    "Duplicate", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            monitorEntry.AddInput(label, vcpValue);
            labelBox.Clear();
            vcpBox.Clear();
        }
    }

    private static bool TryParseVcpValue(string? text, out uint value)
    {
        text = text?.Trim() ?? string.Empty;
        bool parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        return parsed && value <= 255;
    }

    private void RemoveInput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ConfigInputEntry input)
        {
            foreach (var entry in _entries)
            {
                if (entry.Inputs.Contains(input))
                {
                    entry.RemoveInput(input);
                    break;
                }
            }
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ConfigInputEntry input)
        {
            foreach (var entry in _entries)
            {
                int idx = entry.Inputs.IndexOf(input);
                if (idx > 0)
                {
                    entry.Inputs.Move(idx, idx - 1);
                    break;
                }
            }
        }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ConfigInputEntry input)
        {
            foreach (var entry in _entries)
            {
                int idx = entry.Inputs.IndexOf(input);
                if (idx >= 0 && idx < entry.Inputs.Count - 1)
                {
                    entry.Inputs.Move(idx, idx + 1);
                    break;
                }
            }
        }
    }

    private void AddSwitchPreset_Click(object sender, RoutedEventArgs e)
    {
        _presets.Add(new ConfigPresetEntry($"Preset {_presets.Count + 1}", _entries, source: null));
    }

    private void RemoveSwitchPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ConfigPresetEntry preset)
            _presets.Remove(preset);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var config = ConfigService.Load();

        var edited = _entries.Select(entry => new MonitorConfig
        {
            MonitorId = entry.StableId,
            DeviceName = entry.DeviceName,
            DisplayName = entry.DisplayName.Trim(),
            IsDisabled = entry.IsDisabled,
            Inputs = entry.Inputs
                .Select(i => new InputConfig
                {
                    Label = i.Label,
                    VcpValue = i.VcpValue
                }).ToList(),
            RemovedInputs = entry.RemovedInputs.Count > 0 ? entry.RemovedInputs.ToList() : null,
        });

        // Keep settings for monitors that aren't listed here, e.g. one that's unplugged right now.
        var notListed = config.Monitors.Where(c => !_entries.Any(entry => entry.Represents(c.MonitorId, c.DeviceName)));
        config.Monitors = edited.Concat(notListed).ToList();

        config.Presets = _presets.Select((preset, index) => new PresetConfig
        {
            Name = string.IsNullOrWhiteSpace(preset.Name) ? $"Preset {index + 1}" : preset.Name.Trim(),
            Targets = preset.Targets
                .Where(t => t.VcpValue.HasValue)
                .Select(t => new PresetTargetConfig
                {
                    MonitorId = t.Monitor.StableId,
                    DeviceName = t.Monitor.DeviceName,
                    VcpValue = t.VcpValue!.Value,
                })
                .Concat(preset.Source?.Targets.Where(t => !_entries.Any(entry => entry.Represents(t.MonitorId, t.DeviceName)))
                        ?? Enumerable.Empty<PresetTargetConfig>())
                .ToList(),
        }).ToList();

        if (!ConfigService.TrySave(config, out string? error))
        {
            MessageBox.Show($"Couldn't save the configuration:\n\n{error}\n\n{ConfigService.ConfigFilePath}",
                "Save Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SavedConfig = config;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private async void Detect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ConfigMonitorEntry monitorEntry
            && monitorEntry.MonitorInfo != null)
        {
            var monitor = monitorEntry.MonitorInfo;
            fe.IsEnabled = false;
            try
            {
                List<uint> values = new();
                var status = await Task.Run(() => MonitorService.DetectSupportedInputs(monitor, out values));

                if (status != DdcStatus.Ok || values.Count == 0)
                {
                    string reason = status == DdcStatus.NotConnected
                        ? "This monitor isn't connected to this PC right now."
                        : "Could not detect supported inputs for this monitor.\nThe monitor may not support DDC/CI capabilities reporting.";
                    MessageBox.Show(
                        $"{reason}\nUse the dropdown or manual entry instead.",
                        "Detection Failed", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Add detected values that aren't already in the list, including removed ones.
                int added = 0;
                foreach (var vcpValue in values)
                {
                    if (!monitorEntry.Inputs.Any(i => i.VcpValue == vcpValue))
                    {
                        int index = MonitorService.GetDetectedInputIndex(
                            monitorEntry.Inputs.Select(i => i.VcpValue).ToList(), values, vcpValue);
                        monitorEntry.AddInput(MonitorService.GetDefaultLabelForVcpValue(vcpValue), vcpValue, index);
                        added++;
                    }
                }

                MessageBox.Show(
                    $"Detected {values.Count} supported input(s).\n{added} new input(s) added.\n\nYou can rename the labels before saving.",
                    "Detection Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                fe.IsEnabled = true;
            }
        }
    }

    private void Identify_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ConfigMonitorEntry entry && entry.MonitorInfo != null)
        {
            if (!MonitorService.TryGetMonitorBounds(entry.MonitorInfo, out var bounds) || bounds.Width <= 0 || bounds.Height <= 0)
            {
                MessageBox.Show("This monitor isn't connected to this PC right now.",
                    "Identify", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int index = _entries.IndexOf(entry) + 1;
            new IdentifyWindow(index.ToString(), entry.DisplayName, bounds).Show();
        }
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ConfigService.ConfigDirectory);
        Process.Start("explorer.exe", ConfigService.ConfigDirectory);
    }
}

public class InputPreset
{
    public string Label { get; }
    public uint VcpValue { get; }
    public string DisplayText => $"{Label}  (VCP {VcpValue})";

    public InputPreset(string label, uint vcpValue)
    {
        Label = label;
        VcpValue = vcpValue;
    }

    public static List<InputPreset> All { get; } = new()
    {
        new("VGA-1 (Analog)", 1),
        new("VGA-2", 2),
        new("DVI-1", 3),
        new("DVI-2", 4),
        new("Composite-1", 5),
        new("Composite-2", 6),
        new("S-Video-1", 7),
        new("S-Video-2", 8),
        new("Tuner-1", 9),
        new("Tuner-2", 10),
        new("Component-1", 11),
        new("Component-2", 12),
        new("DisplayPort-1", 15),
        new("DisplayPort-2", 16),
        new("HDMI-1", 17),
        new("HDMI-2", 18),
        new("USB-C / DP Alt", 27),
    };

    public override bool Equals(object? obj)
        => obj is InputPreset other && VcpValue == other.VcpValue;

    public override int GetHashCode() => VcpValue.GetHashCode();
}

public class ConfigMonitorEntry : INotifyPropertyChanged
{
    private string _displayName = string.Empty;
    private bool _isDisabled;

    public string Id { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public bool IsConnected { get; set; } = true;
    public MonitorInfo? MonitorInfo { get; set; }

    public string HeaderText => IsConnected ? DeviceName : $"{DeviceName}  (not connected)";

    /// <summary>The id to save, or null when only the display name identifies the monitor.</summary>
    public string? StableId => MonitorService.IsStableId(Id) ? Id : null;

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName != value)
            {
                _displayName = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsDisabled
    {
        get => _isDisabled;
        set
        {
            if (_isDisabled != value)
            {
                _isDisabled = value;
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<ConfigInputEntry> Inputs { get; set; } = new();

    /// <summary>VCP values the user removed, which automatic detection won't add back.</summary>
    public List<uint> RemovedInputs { get; set; } = new();

    public void AddInput(string label, uint vcpValue, int index = -1)
    {
        Inputs.Insert(index < 0 ? Inputs.Count : index, new ConfigInputEntry(label, vcpValue));
        RemovedInputs.Remove(vcpValue);
    }

    public void RemoveInput(ConfigInputEntry input)
    {
        Inputs.Remove(input);
        if (!RemovedInputs.Contains(input.VcpValue))
            RemovedInputs.Add(input.VcpValue);
    }

    /// <summary>Whether a saved entry (by monitor id, or display name for old entries) refers to this monitor.</summary>
    public bool Represents(string? monitorId, string deviceName) =>
        !string.IsNullOrEmpty(monitorId)
            ? string.Equals(monitorId, Id, StringComparison.OrdinalIgnoreCase)
            : string.Equals(deviceName, DeviceName, StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class ConfigInputEntry : INotifyPropertyChanged
{
    private string _label;

    public string Label
    {
        get => _label;
        set
        {
            if (_label != value)
            {
                _label = value;
                OnPropertyChanged();
            }
        }
    }

    public uint VcpValue { get; }
    public string VcpDisplay => $"(VCP {VcpValue})";

    public ConfigInputEntry(string label, uint vcpValue)
    {
        _label = label;
        VcpValue = vcpValue;
    }

    // Name read by screen readers in the preset dropdowns.
    public override string ToString() => Label;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class ConfigPresetEntry : INotifyPropertyChanged
{
    private string _name;

    public ConfigPresetEntry(string name, IEnumerable<ConfigMonitorEntry> monitors, PresetConfig? source)
    {
        _name = name;
        Source = source;
        Targets = new ObservableCollection<ConfigPresetTarget>(monitors.Select(monitor =>
            new ConfigPresetTarget(monitor,
                source?.Targets.FirstOrDefault(t => monitor.Represents(t.MonitorId, t.DeviceName))?.VcpValue)));
    }

    /// <summary>The saved preset this was loaded from, to keep targets for monitors not shown.</summary>
    public PresetConfig? Source { get; }

    public ObservableCollection<ConfigPresetTarget> Targets { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One monitor's choice within a preset: one of its inputs, or "Don't change".</summary>
public class ConfigPresetTarget : INotifyPropertyChanged
{
    public static readonly DontChangeOption DontChange = new();

    private object? _selected;

    public ConfigPresetTarget(ConfigMonitorEntry monitor, uint? vcpValue)
    {
        Monitor = monitor;
        RebuildOptions(vcpValue);

        // Keep the choices in sync while inputs are added or removed in this window.
        monitor.Inputs.CollectionChanged += (_, _) => RebuildOptions(VcpValue);
    }

    public ConfigMonitorEntry Monitor { get; }
    public ObservableCollection<object> Options { get; } = new();

    public object? Selected
    {
        get => _selected;
        set
        {
            if (_selected != value)
            {
                _selected = value;
                OnPropertyChanged();
            }
        }
    }

    public uint? VcpValue => (Selected as ConfigInputEntry)?.VcpValue;

    private void RebuildOptions(uint? keepVcpValue)
    {
        Options.Clear();
        Options.Add(DontChange);
        foreach (var input in Monitor.Inputs)
            Options.Add(input);

        Selected = (object?)Monitor.Inputs.FirstOrDefault(i => i.VcpValue == keepVcpValue) ?? DontChange;
    }

    public class DontChangeOption
    {
        public string Label => "Don't change";

        public override string ToString() => Label;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
