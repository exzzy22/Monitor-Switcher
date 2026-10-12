using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MonitorSwitcher.Core;
using MonitorSwitcher.Views;

namespace MonitorSwitcher.ViewModels;

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan ActivationReadInterval = TimeSpan.FromSeconds(2);

    // Gives a click that activated the window time to send its switch before we read.
    private static readonly TimeSpan ActivationReadDelay = TimeSpan.FromMilliseconds(400);

    private readonly List<MonitorViewModel> _allMonitorViewModels = new();

    // Inputs each monitor reported in its DDC/CI capabilities this session, by monitor id.
    private readonly Dictionary<string, List<uint>> _detectedInputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _detectionStarted = new(StringComparer.OrdinalIgnoreCase);

    private readonly DispatcherTimer _displayChangeTimer;
    private AppConfig _config = new();
    private CancellationTokenSource? _refreshCts;
    private DateTime _lastActivationReadUtc = DateTime.MinValue;
    private bool _isInitialized;
    private bool _isRefreshing;
    private bool _isTopMost;
    private bool _isConfigOpen;
    private bool _disposed;

    public MainViewModel()
    {
        Monitors = new ObservableCollection<MonitorViewModel>();
        Presets = new ObservableCollection<PresetViewModel>();
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        ConfigureCommand = new RelayCommand(OpenConfigure);
        ToggleTopMostCommand = new RelayCommand(() => IsTopMost = !IsTopMost);

        // Windows sends several display-change messages while monitors come and go;
        // wait for them to settle before re-enumerating.
        _displayChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _displayChangeTimer.Tick += (_, _) =>
        {
            _displayChangeTimer.Stop();
            _ = RefreshAsync();
        };
    }

    public ObservableCollection<MonitorViewModel> Monitors { get; }
    public ObservableCollection<PresetViewModel> Presets { get; }
    public bool HasPresets => Presets.Count > 0;
    public ICommand RefreshCommand { get; }
    public ICommand ConfigureCommand { get; }
    public ICommand ToggleTopMostCommand { get; }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (_isRefreshing != value)
            {
                _isRefreshing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowNoMonitorsMessage));
            }
        }
    }

    public bool ShowNoMonitorsMessage => _isInitialized && !IsRefreshing && Monitors.Count == 0;

    public bool IsTopMost
    {
        get => _isTopMost;
        set
        {
            if (_isTopMost != value)
            {
                _isTopMost = value;
                OnPropertyChanged();

                _config.AlwaysOnTop = value;
                ConfigService.TrySave(_config, out _);
            }
        }
    }

    public async Task InitializeAsync()
    {
        _config = await Task.Run(() => ConfigService.Load());
        bool firstRun = !ConfigService.ConfigExists();

        _isTopMost = _config.AlwaysOnTop;
        OnPropertyChanged(nameof(IsTopMost));
        LoadPresets();

        var token = await ReloadMonitorsAsync();
        _isInitialized = true;
        OnPropertyChanged(nameof(ShowNoMonitorsMessage));

        await ReadAllInputsAsync(token);
        await DetectInputsAsync();

        // Inputs are detected automatically, so setup is only needed when a monitor didn't report them.
        if (firstRun && Monitors.Any(m => m.IsConnected && m.Inputs.Count == 0))
            OpenConfigure();
    }

    /// <summary>Re-enumerates monitors (handles plug/unplug) and re-reads their inputs.</summary>
    public async Task RefreshAsync()
    {
        if (_disposed)
            return;

        var token = await ReloadMonitorsAsync();
        await ReadAllInputsAsync(token);
        await DetectInputsAsync();
    }

    /// <summary>
    /// Re-reads current inputs when the window is activated, to pick up changes made
    /// elsewhere (monitor buttons, another PC, auto input select).
    /// </summary>
    public void OnWindowActivated()
    {
        if (_disposed || !_isInitialized || IsRefreshing || _isConfigOpen)
            return;

        var now = DateTime.UtcNow;
        if (now - _lastActivationReadUtc < ActivationReadInterval)
            return;
        _lastActivationReadUtc = now;

        _ = ReadInputsQuietlyAsync();
    }

    /// <summary>Called when Windows reports a display change or the PC resumes from sleep.</summary>
    public void OnDisplaySettingsChanged()
    {
        if (_disposed)
            return;

        _displayChangeTimer.Stop();
        _displayChangeTimer.Start();
    }

    private async Task<CancellationToken> ReloadMonitorsAsync()
    {
        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;

        IsRefreshing = true;
        try
        {
            var monitors = await Task.Run(() => MonitorService.GetMonitors());
            if (cts.IsCancellationRequested)
                return cts.Token;

            // Keep showing monitors that disappeared while the app was open, so it's clear
            // why they can't be switched (typically a DisplayPort monitor on another source).
            foreach (var vm in _allMonitorViewModels)
            {
                if (!monitors.Exists(m => SameId(m.Id, vm.Id)))
                    monitors.Add(vm.MonitorInfo.CloneAsDisconnected());
            }

            if (MergeConfig(monitors, _config, _detectedInputs))
                ConfigService.TrySave(_config, out _);

            ApplyMonitors(monitors);
            Log.Info("Monitors: " + string.Join("; ", monitors.Select(m =>
                $"{m.Name} [{m.DeviceName}{(m.IsConnected ? "" : ", not connected")}]")));
        }
        catch (Exception ex)
        {
            Log.Error("Refreshing monitors failed", ex);
        }
        finally
        {
            if (_refreshCts == cts)
                IsRefreshing = false;
        }

        return cts.Token;
    }

    private void ApplyMonitors(List<MonitorInfo> monitors)
    {
        var previous = new List<MonitorViewModel>(_allMonitorViewModels);
        _allMonitorViewModels.Clear();

        foreach (var info in monitors)
        {
            var vm = previous.Find(v => SameId(v.Id, info.Id));
            if (vm != null)
            {
                vm.Update(info);
            }
            else
            {
                vm = new MonitorViewModel(info);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MonitorViewModel.CurrentInput))
                        UpdatePresetStates();
                };
            }
            _allMonitorViewModels.Add(vm);
        }

        var visible = _allMonitorViewModels.Where(vm => !vm.MonitorInfo.IsDisabled).ToList();
        if (!visible.SequenceEqual(Monitors))
        {
            Monitors.Clear();
            foreach (var vm in visible)
                Monitors.Add(vm);
        }

        OnPropertyChanged(nameof(ShowNoMonitorsMessage));
        UpdatePresetStates();
    }

    private void LoadPresets()
    {
        Presets.Clear();
        foreach (var preset in _config.Presets.Where(p => p.Targets.Count > 0))
            Presets.Add(new PresetViewModel(preset, p => _ = ApplyPresetAsync(p)));

        OnPropertyChanged(nameof(HasPresets));
        UpdatePresetStates();
    }

    private async Task ApplyPresetAsync(PresetViewModel preset)
    {
        Log.Info($"Applying preset \"{preset.Label}\"");
        var switches = new List<Task>();

        foreach (var target in preset.Config.Targets)
        {
            var vm = FindMonitor(target);
            if (vm == null)
            {
                Log.Warn($"Preset \"{preset.Label}\": monitor {target.MonitorId ?? target.DeviceName} isn't connected");
                continue;
            }

            // Send every command right away rather than one after another: once one
            // monitor switches away, Windows may rearrange displays, which can briefly
            // disrupt talking to the others.
            switches.Add(vm.SwitchToAsync(target.VcpValue));
        }

        await Task.WhenAll(switches);
    }

    private void UpdatePresetStates()
    {
        foreach (var preset in Presets)
        {
            preset.IsActive = preset.Config.Targets.All(target =>
                FindMonitor(target) is { IsConnected: true } vm && vm.CurrentInput == target.VcpValue);
        }
    }

    private MonitorViewModel? FindMonitor(PresetTargetConfig target) =>
        _allMonitorViewModels.Find(vm => !string.IsNullOrEmpty(target.MonitorId)
            ? SameId(vm.Id, target.MonitorId)
            : string.Equals(vm.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase));

    private async Task ReadAllInputsAsync(CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return;

        IsRefreshing = true;
        try
        {
            // Each monitor has its own DDC/CI bus, so they can be read in parallel.
            await Task.WhenAll(Monitors.ToList().Select(vm => vm.ReadCurrentInputAsync(attempts: 2, quiet: false, token)));
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsRefreshing = false;
        }
    }

    private async Task ReadInputsQuietlyAsync()
    {
        await Task.Delay(ActivationReadDelay);
        if (_disposed || IsRefreshing)
            return;

        await Task.WhenAll(Monitors.ToList().Select(vm => vm.ReadCurrentInputAsync(attempts: 1, quiet: true, CancellationToken.None)));
    }

    /// <summary>
    /// Asks each connected, visible monitor which inputs it has (once per session) and adds
    /// any that aren't listed yet, so inputs don't have to be set up by hand.
    /// </summary>
    private async Task DetectInputsAsync()
    {
        var pending = _allMonitorViewModels
            .Where(vm => vm.IsConnected && !vm.MonitorInfo.IsDisabled && _detectionStarted.Add(vm.Id))
            .Select(vm => vm.MonitorInfo)
            .ToList();
        if (pending.Count == 0)
            return;

        // Each monitor has its own DDC/CI bus, so they can be queried in parallel.
        var results = await Task.WhenAll(pending.Select(monitor => Task.Run(() =>
        {
            var status = MonitorService.DetectSupportedInputs(monitor, out var values);
            return (Monitor: monitor, Status: status, Values: values);
        })));

        bool detectedAny = false;
        foreach (var (monitor, status, values) in results)
        {
            if (status == DdcStatus.Ok && values.Count > 0)
            {
                _detectedInputs[monitor.Id] = values;
                detectedAny = true;
                continue;
            }

            Log.Warn($"Couldn't detect the inputs of {monitor.Name}: "
                + (status == DdcStatus.Ok ? "its capabilities don't list any" : status.ToString()));

            // Showing another device or busy right now; try again when it's back.
            if (status is DdcStatus.NotConnected or DdcStatus.Busy)
                _detectionStarted.Remove(monitor.Id);
        }

        if (!detectedAny || _disposed)
            return;

        var monitors = _allMonitorViewModels.Select(vm => vm.MonitorInfo).ToList();
        if (MergeConfig(monitors, _config, _detectedInputs))
            ConfigService.TrySave(_config, out _);
        ApplyMonitors(monitors);
    }

    private void OpenConfigure()
    {
        if (_isConfigOpen)
            return;

        _isConfigOpen = true;
        try
        {
            var configWindow = new ConfigWindow(_allMonitorViewModels.ToList())
            {
                Owner = Application.Current.MainWindow,
            };

            if (configWindow.ShowDialog() == true && configWindow.SavedConfig != null)
            {
                _config = configWindow.SavedConfig;
                LoadPresets();
                _ = RefreshAsync();
            }
        }
        finally
        {
            _isConfigOpen = false;
        }
    }

    /// <summary>
    /// Applies saved names, inputs and visibility to the detected monitors, and adds inputs
    /// the monitors reported that aren't saved yet. Returns true when the config changed and
    /// should be saved.
    /// </summary>
    private static bool MergeConfig(List<MonitorInfo> monitors, AppConfig config,
        IReadOnlyDictionary<string, List<uint>> detectedInputs)
    {
        bool configChanged = false;
        var unmatchedConfigs = new List<MonitorConfig>(config.Monitors);
        var unmatchedMonitors = new List<MonitorInfo>();
        var applied = new Dictionary<MonitorInfo, MonitorConfig>();

        // 1. By stable monitor id.
        foreach (var monitor in monitors)
        {
            var match = unmatchedConfigs.Find(c => !string.IsNullOrEmpty(c.MonitorId) && SameId(c.MonitorId, monitor.Id));
            if (match != null)
            {
                Apply(monitor, match);
                unmatchedConfigs.Remove(match);
            }
            else
            {
                unmatchedMonitors.Add(monitor);
            }
        }

        // 2. Entries saved by older versions only have the display name (\\.\DISPLAY1).
        //    Match them once and record the monitor id, which survives display renumbering.
        foreach (var monitor in unmatchedMonitors.Where(m => m.IsConnected).ToList())
        {
            var match = unmatchedConfigs.Find(c => string.IsNullOrEmpty(c.MonitorId)
                && string.Equals(c.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (match == null)
                continue;

            if (MonitorService.IsStableId(monitor.Id))
            {
                foreach (var target in config.Presets.SelectMany(p => p.Targets)
                             .Where(t => string.IsNullOrEmpty(t.MonitorId)
                                 && string.Equals(t.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase)))
                {
                    target.MonitorId = monitor.Id;
                }
                match.MonitorId = monitor.Id;
                configChanged = true;
            }
            Apply(monitor, match);
            unmatchedConfigs.Remove(match);
            unmatchedMonitors.Remove(monitor);
        }

        // 3. Same monitor model on a different port (cable moved), when that's unambiguous.
        foreach (var monitor in unmatchedMonitors.Where(m => m.IsConnected).ToList())
        {
            string hardwareId = MonitorService.GetHardwareId(monitor.Id);
            if (hardwareId.Length == 0
                || unmatchedMonitors.Count(m => m.IsConnected && MonitorService.GetHardwareId(m.Id) == hardwareId) != 1)
            {
                continue;
            }

            var candidates = unmatchedConfigs.Where(c => MonitorService.GetHardwareId(c.MonitorId) == hardwareId).ToList();
            if (candidates.Count != 1)
                continue;

            foreach (var target in config.Presets.SelectMany(p => p.Targets).Where(t => SameId(t.MonitorId, candidates[0].MonitorId)))
            {
                target.MonitorId = monitor.Id;
                target.DeviceName = monitor.DeviceName;
            }
            candidates[0].MonitorId = monitor.Id;
            candidates[0].DeviceName = monitor.DeviceName;
            configChanged = true;
            Apply(monitor, candidates[0]);
            unmatchedConfigs.Remove(candidates[0]);
            unmatchedMonitors.Remove(monitor);
        }

        // 4. Inputs the monitor reported that aren't saved yet, unless the user removed them.
        foreach (var monitor in monitors.Where(m => m.IsConnected))
        {
            if (!detectedInputs.TryGetValue(monitor.Id, out var detected))
                continue;

            if (!applied.TryGetValue(monitor, out var monitorConfig))
            {
                monitorConfig = new MonitorConfig
                {
                    MonitorId = MonitorService.IsStableId(monitor.Id) ? monitor.Id : null,
                    DeviceName = monitor.DeviceName,
                };
            }

            var added = AddDetectedInputs(monitorConfig, detected);
            if (added.Count == 0)
                continue;

            if (!config.Monitors.Contains(monitorConfig))
                config.Monitors.Add(monitorConfig);
            Apply(monitor, monitorConfig);
            configChanged = true;
            Log.Info($"Added inputs detected on {monitor.Name}: {string.Join(", ", added.Select(i => i.Label))}");
        }

        return configChanged;

        void Apply(MonitorInfo monitor, MonitorConfig saved)
        {
            applied[monitor] = saved;

            if (!string.IsNullOrWhiteSpace(saved.DisplayName))
                monitor.Name = saved.DisplayName;

            monitor.Inputs = saved.Inputs
                .Select(i => new InputEntry { Label = i.Label, VcpValue = i.VcpValue })
                .ToList();

            monitor.RemovedInputs = saved.RemovedInputs?.ToList() ?? new List<uint>();
            monitor.IsDisabled = saved.IsDisabled;
        }
    }

    /// <summary>
    /// Adds detected VCP values the config doesn't list or the user hasn't removed, keeping
    /// the order the monitor reports them in. Returns the added inputs.
    /// </summary>
    private static List<InputConfig> AddDetectedInputs(MonitorConfig config, List<uint> detected)
    {
        var added = new List<InputConfig>();
        foreach (uint value in detected)
        {
            if (config.Inputs.Any(i => i.VcpValue == value) || config.RemovedInputs?.Contains(value) == true)
                continue;

            int index = MonitorService.GetDetectedInputIndex(config.Inputs.Select(i => i.VcpValue).ToList(), detected, value);
            var input = new InputConfig { Label = MonitorService.GetDefaultLabelForVcpValue(value), VcpValue = value };
            config.Inputs.Insert(index, input);
            added.Add(input);
        }
        return added;
    }

    private static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _disposed = true;
        _displayChangeTimer.Stop();
        _refreshCts?.Cancel();
        _refreshCts = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
