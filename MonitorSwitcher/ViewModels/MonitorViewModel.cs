using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MonitorSwitcher.Core;

namespace MonitorSwitcher.ViewModels;

public class MonitorViewModel : INotifyPropertyChanged
{
    // A monitor needs a moment after switching before DDC/CI reads are meaningful again;
    // routine refreshes in that window would overwrite the new input with a stale one.
    private static readonly TimeSpan SwitchSettleTime = TimeSpan.FromSeconds(3);

    private const string DisconnectedMessage =
        "Not connected to this PC right now. If it's showing another device, switch it back with the monitor's buttons.";

    private readonly RelayCommand _setInputCommand;
    private MonitorInfo _monitor;
    private string _displayName;
    private string _currentInputLabel = "unknown";
    private string? _statusMessage;
    private bool _isConnected;
    private bool _isSwitching;
    private int _activeReads;
    private uint _currentInput;
    private int _switchGeneration;
    private DateTime _lastSwitchUtc = DateTime.MinValue;

    public MonitorViewModel(MonitorInfo monitor)
    {
        _monitor = monitor;
        _displayName = monitor.Name;
        _isConnected = monitor.IsConnected;
        _currentInput = monitor.CurrentInput;
        _statusMessage = monitor.IsConnected ? null : DisconnectedMessage;

        Inputs = new ObservableCollection<InputEntryViewModel>(
            monitor.Inputs.Select(i => new InputEntryViewModel(i.Label, i.VcpValue)));

        _setInputCommand = new RelayCommand(
            o => { if (o is InputEntryViewModel entry) _ = SwitchToAsync(entry.VcpValue); },
            _ => CanSwitch);

        UpdateCurrentInputLabel();
        UpdateActiveStates();
    }

    public string Id => _monitor.Id;
    public string DeviceName => _monitor.DeviceName;
    public MonitorInfo MonitorInfo => _monitor;
    public ObservableCollection<InputEntryViewModel> Inputs { get; }
    public ICommand SetInputCommand => _setInputCommand;

    public string DisplayName
    {
        get => _displayName;
        private set => SetField(ref _displayName, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetField(ref _isConnected, value))
                OnCanSwitchChanged();
        }
    }

    public bool IsSwitching
    {
        get => _isSwitching;
        private set
        {
            if (SetField(ref _isSwitching, value))
                OnCanSwitchChanged();
        }
    }

    public bool CanSwitch => IsConnected && !IsSwitching;

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetField(ref _statusMessage, value))
                OnPropertyChanged(nameof(HasStatusMessage));
        }
    }

    public bool HasStatusMessage => !string.IsNullOrEmpty(_statusMessage);

    public string CurrentInputLabel
    {
        get => _currentInputLabel;
        private set => SetField(ref _currentInputLabel, value);
    }

    public uint CurrentInput
    {
        get => _currentInput;
        private set
        {
            _currentInput = value;
            _monitor.CurrentInput = value;
            OnPropertyChanged();
            UpdateCurrentInputLabel();
            UpdateActiveStates();
        }
    }

    /// <summary>Applies freshly enumerated (and config-merged) info for this monitor.</summary>
    public void Update(MonitorInfo fresh)
    {
        bool wasConnected = IsConnected;
        fresh.CurrentInput = _currentInput;
        _monitor = fresh;

        DisplayName = fresh.Name;
        IsConnected = fresh.IsConnected;

        if (!fresh.IsConnected)
            StatusMessage = DisconnectedMessage;
        else if (!wasConnected || StatusMessage == DisconnectedMessage)
            StatusMessage = null;

        bool inputsChanged = Inputs.Count != fresh.Inputs.Count
            || Inputs.Where((input, i) => input.Label != fresh.Inputs[i].Label || input.VcpValue != fresh.Inputs[i].VcpValue).Any();
        if (inputsChanged)
        {
            Inputs.Clear();
            foreach (var input in fresh.Inputs)
                Inputs.Add(new InputEntryViewModel(input.Label, input.VcpValue));
        }

        UpdateCurrentInputLabel();
        UpdateActiveStates();
    }

    /// <summary>
    /// Reads the monitor's current input.
    /// </summary>
    /// <param name="attempts">DDC/CI reads fail transiently now and then, so allow retries.</param>
    /// <param name="quiet">Keep the last known input if the read fails (background refreshes).</param>
    public async Task ReadCurrentInputAsync(int attempts, bool quiet, CancellationToken token)
    {
        if (!IsConnected || IsSwitching || DateTime.UtcNow - _lastSwitchUtc < SwitchSettleTime)
            return;

        int generation = _switchGeneration;
        var monitor = _monitor;
        if (!quiet)
            ChangeActiveReads(+1);

        try
        {
            uint value = 0;
            for (int attempt = 0; attempt < attempts && value == 0; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(300, token);
                value = await Task.Run(() => MonitorService.ReadCurrentInput(monitor), token);
            }

            // A switch started while we were reading, so this value is already out of date.
            if (token.IsCancellationRequested || generation != _switchGeneration)
                return;

            if (value != 0 || !quiet)
                CurrentInput = value;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!quiet)
                ChangeActiveReads(-1);
        }
    }

    /// <summary>Switches the monitor to the input with the given VCP 0x60 value.</summary>
    public async Task SwitchToAsync(uint vcpValue)
    {
        if (!CanSwitch)
            return;

        int generation = ++_switchGeneration;
        uint previousInput = _currentInput;
        var monitor = _monitor;

        IsSwitching = true;
        StatusMessage = null;
        _lastSwitchUtc = DateTime.UtcNow;

        // Show the new input right away; the monitor may take a few seconds to settle.
        CurrentInput = vcpValue;

        DdcStatus status;
        try
        {
            status = await Task.Run(() => MonitorService.SetInput(monitor, vcpValue));
        }
        catch (Exception ex)
        {
            Log.Error($"Switching {monitor.Name} failed", ex);
            status = DdcStatus.Failed;
        }
        finally
        {
            IsSwitching = false;
        }

        if (status != DdcStatus.Ok)
        {
            if (generation == _switchGeneration)
            {
                CurrentInput = previousInput;
                StatusMessage = DescribeFailure(status);
            }
            return;
        }

        // Confirm once the monitor has settled. If it bounced back (e.g. no signal on
        // that input and auto-select is on), this shows the input it's really on.
        _lastSwitchUtc = DateTime.UtcNow;
        await Task.Delay(SwitchSettleTime);
        if (generation != _switchGeneration || !IsConnected)
            return;

        uint actual = await Task.Run(() => MonitorService.ReadCurrentInput(monitor));
        if (generation == _switchGeneration && actual != 0)
            CurrentInput = actual;
    }

    private static string DescribeFailure(DdcStatus status) => status switch
    {
        DdcStatus.NotConnected => "Windows can't see this monitor right now, so it can't be switched from this PC.",
        DdcStatus.NoDdc => "Windows can't talk to this monitor over DDC/CI. Check that DDC/CI is enabled in the monitor's menu.",
        DdcStatus.Busy => "The monitor is still busy with a previous command. Try again in a moment.",
        _ => "The monitor didn't accept the switch. Try again; if it keeps failing, check that DDC/CI is enabled in the monitor's menu.",
    };

    private void ChangeActiveReads(int delta)
    {
        _activeReads += delta;
        UpdateCurrentInputLabel();
    }

    private void OnCanSwitchChanged()
    {
        OnPropertyChanged(nameof(CanSwitch));
        _setInputCommand.RaiseCanExecuteChanged();
    }

    private void UpdateCurrentInputLabel()
    {
        if (_currentInput == 0)
        {
            CurrentInputLabel = _activeReads > 0 ? "checking…" : "unknown";
            return;
        }

        var match = Inputs.FirstOrDefault(i => i.VcpValue == _currentInput);
        CurrentInputLabel = match != null ? match.Label : $"VCP {_currentInput}";
    }

    private void UpdateActiveStates()
    {
        foreach (var input in Inputs)
        {
            input.IsActive = input.VcpValue == _currentInput;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
