using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MonitorSwitcher.Core;

namespace MonitorSwitcher.ViewModels;

/// <summary>A one-click button that switches several monitors at once.</summary>
public class PresetViewModel : INotifyPropertyChanged
{
    private bool _isActive;

    public PresetViewModel(PresetConfig config, Action<PresetViewModel> apply)
    {
        Config = config;
        ApplyCommand = new RelayCommand(() => apply(this));
    }

    public PresetConfig Config { get; }
    public string Label => Config.Name;
    public ICommand ApplyCommand { get; }

    public string ToolTip => Config.Targets.Count == 1
        ? "Switches 1 monitor"
        : $"Switches {Config.Targets.Count} monitors";

    /// <summary>True when every monitor in the preset is already on its preset input.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
