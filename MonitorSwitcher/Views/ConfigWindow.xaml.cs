using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using MonitorSwitcher.Core;
using MonitorSwitcher.ViewModels;

namespace MonitorSwitcher.Views;

public partial class ConfigWindow : Window
{
    private readonly List<ConfigMonitorEntry> _entries;

    public ConfigWindow(List<MonitorViewModel> monitors)
    {
        InitializeComponent();

        _entries = monitors.Select(m => new ConfigMonitorEntry
        {
            DeviceName = m.DeviceName,
            DisplayName = m.DisplayName,
            Inputs = new ObservableCollection<ConfigInputEntry>(
                m.MonitorInfo.Inputs.Select(i => new ConfigInputEntry(i.Label, i.VcpValue)))
        }).ToList();

        MonitorList.ItemsSource = _entries;
    }

    private void AddInputCombo_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox combo)
        {
            combo.ItemsSource = InputPreset.All;
        }
    }

    private void AddPreset_Click(object sender, RoutedEventArgs e)
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

                monitorEntry.Inputs.Add(new ConfigInputEntry(preset.Label, preset.VcpValue));
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
            var labelBox = sp.Children.OfType<TextBox>().FirstOrDefault(t => t.Name == "ManualLabel");
            var vcpBox = sp.Children.OfType<TextBox>().FirstOrDefault(t => t.Name == "ManualVcp");
            var monitorEntry = sp.Tag as ConfigMonitorEntry
                ?? labelBox?.Tag as ConfigMonitorEntry
                ?? vcpBox?.Tag as ConfigMonitorEntry;

            if (monitorEntry == null || labelBox == null || vcpBox == null)
                return;

            string label = labelBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(label))
            {
                MessageBox.Show("Enter a label for the input.",
                    "Missing Label", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!uint.TryParse(vcpBox.Text, out uint vcpValue) || vcpValue > 255)
            {
                MessageBox.Show("VCP value must be a number between 0 and 255.",
                    "Invalid Value", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (monitorEntry.Inputs.Any(i => i.VcpValue == vcpValue))
            {
                MessageBox.Show($"VCP {vcpValue} is already added.",
                    "Duplicate", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            monitorEntry.Inputs.Add(new ConfigInputEntry(label, vcpValue));
            labelBox.Clear();
            vcpBox.Clear();
        }
    }

    private void RemoveInput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ConfigInputEntry input)
        {
            foreach (var entry in _entries)
            {
                if (entry.Inputs.Contains(input))
                {
                    entry.Inputs.Remove(input);
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var configs = _entries.Select(entry => new MonitorConfig
        {
            DeviceName = entry.DeviceName,
            DisplayName = entry.DisplayName,
            Inputs = entry.Inputs
                .Select(i => new InputConfig
                {
                    Label = i.Label,
                    VcpValue = i.VcpValue
                }).ToList()
        }).ToList();

        ConfigService.SaveConfig(configs);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
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

    public string DeviceName { get; set; } = string.Empty;

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

    public ObservableCollection<ConfigInputEntry> Inputs { get; set; } = new();

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

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
