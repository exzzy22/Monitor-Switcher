using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MonitorSwitcher.ViewModels;

public class InputEntryViewModel : INotifyPropertyChanged
{
    private bool _isActive;

    public string Label { get; }
    public uint VcpValue { get; }

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

    public InputEntryViewModel(string label, uint vcpValue)
    {
        Label = label;
        VcpValue = vcpValue;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
