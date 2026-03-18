using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MonitorSwitcher.Core;

namespace MonitorSwitcher.ViewModels;

public class MonitorViewModel : INotifyPropertyChanged
{
    private readonly MonitorInfo _monitor;
    private string _currentInputLabel = "unknown";
    private bool _isBusy;
    private uint _currentInput;

    public MonitorViewModel(MonitorInfo monitor)
    {
        _monitor = monitor;
        _currentInput = monitor.CurrentInput;
        DisplayName = monitor.Name;
        DeviceName = monitor.DeviceName;

        Inputs = new ObservableCollection<InputEntryViewModel>();
        foreach (var input in monitor.Inputs)
        {
            Inputs.Add(new InputEntryViewModel(input.Label, input.VcpValue));
        }

        SetInputCommand = new RelayCommand(
            o => SetInput(o),
            o => !IsBusy);

        UpdateCurrentInputLabel();
        UpdateActiveStates();
    }

    public string DisplayName { get; }
    public string DeviceName { get; }
    public ObservableCollection<InputEntryViewModel> Inputs { get; }
    public ICommand SetInputCommand { get; }

    public string CurrentInputLabel
    {
        get => _currentInputLabel;
        private set
        {
            if (_currentInputLabel != value)
            {
                _currentInputLabel = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy != value)
            {
                _isBusy = value;
                OnPropertyChanged();
            }
        }
    }

    public uint CurrentInput
    {
        get => _currentInput;
        set
        {
            _currentInput = value;
            _monitor.CurrentInput = value;
            OnPropertyChanged();
            UpdateCurrentInputLabel();
            UpdateActiveStates();
        }
    }

    public MonitorInfo MonitorInfo => _monitor;

    public void RefreshInputs()
    {
        Inputs.Clear();
        foreach (var input in _monitor.Inputs)
        {
            Inputs.Add(new InputEntryViewModel(input.Label, input.VcpValue));
        }
        UpdateActiveStates();
    }

    private void UpdateCurrentInputLabel()
    {
        if (_currentInput == 0)
        {
            CurrentInputLabel = "unknown";
            return;
        }

        // Check VM inputs first (always up to date), then fall back to model
        var vmMatch = Inputs.FirstOrDefault(i => i.VcpValue == _currentInput);
        if (vmMatch != null)
        {
            CurrentInputLabel = vmMatch.Label;
            return;
        }

        var modelMatch = _monitor.Inputs.Find(i => i.VcpValue == _currentInput);
        CurrentInputLabel = modelMatch != null ? modelMatch.Label : $"VCP {_currentInput}";
    }

    private void UpdateActiveStates()
    {
        foreach (var input in Inputs)
        {
            input.IsActive = input.VcpValue == _currentInput;
        }
    }

    private async void SetInput(object? parameter)
    {
        if (parameter is not InputEntryViewModel entry)
            return;

        IsBusy = true;
        try
        {
            // Optimistically set the active state immediately so the UI responds.
            // Trust the user's intent — the actual state will be verified when
            // the window is reactivated (handles monitor signal renegotiation).
            CurrentInput = entry.VcpValue;

            await Task.Run(() => MonitorService.SetInput(_monitor, entry.VcpValue));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
