using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using MonitorSwitcher.Core;
using MonitorSwitcher.Views;

namespace MonitorSwitcher.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private bool _isRefreshing;
    private bool _isTopMost;
    private CancellationTokenSource? _refreshCts;

    public MainViewModel()
    {
        Monitors = new ObservableCollection<MonitorViewModel>();
        ConfigureCommand = new RelayCommand(OpenConfigure);
        ToggleTopMostCommand = new RelayCommand(() => IsTopMost = !IsTopMost);
    }

    public ObservableCollection<MonitorViewModel> Monitors { get; }
    public ICommand ConfigureCommand { get; }
    public ICommand ToggleTopMostCommand { get; }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set
        {
            if (_isRefreshing != value)
            {
                _isRefreshing = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsTopMost
    {
        get => _isTopMost;
        set
        {
            if (_isTopMost != value)
            {
                _isTopMost = value;
                OnPropertyChanged();
            }
        }
    }

    public async Task InitializeAsync()
    {
        var monitors = MonitorService.GetMonitors();
        var configs = ConfigService.LoadConfig();
        MergeConfig(monitors, configs);

        foreach (var m in monitors)
        {
            Monitors.Add(new MonitorViewModel(m));
        }

        // First-run: open config window if no saved config
        if (!ConfigService.ConfigExists())
        {
            OpenConfigure();
        }

        await ReadAllInputsAsync();
    }

    public async Task RefreshAsync()
    {
        // Cancel any previous refresh
        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        var token = _refreshCts.Token;

        IsRefreshing = true;
        try
        {
            // Re-enumerate monitors
            var monitors = await Task.Run(() => MonitorService.GetMonitors(), token);
            if (token.IsCancellationRequested) return;

            var configs = ConfigService.LoadConfig();
            MergeConfig(monitors, configs);

            Application.Current.Dispatcher.Invoke(() =>
            {
                Monitors.Clear();
                foreach (var m in monitors)
                {
                    Monitors.Add(new MonitorViewModel(m));
                }
            });

            if (token.IsCancellationRequested) return;

            await ReadAllInputsAsync(token);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>
    /// Lightweight re-read of current inputs without re-enumerating monitors.
    /// Called when the window regains focus to pick up any changes
    /// (e.g. monitor auto-switched back to an active signal).
    /// </summary>
    public async Task SilentReadInputsAsync()
    {
        foreach (var vm in Monitors.ToList())
        {
            if (vm.IsBusy) continue;

            try
            {
                var monitor = vm.MonitorInfo;
                uint input = await Task.Run(() => MonitorService.ReadCurrentInput(monitor));

                if (input != 0)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        vm.CurrentInput = input;
                    });
                }
            }
            catch
            {
                // Silently ignore — this is a best-effort refresh
            }
        }
    }

    private async Task ReadAllInputsAsync(CancellationToken token = default)
    {
        foreach (var vm in Monitors.ToList())
        {
            if (token.IsCancellationRequested) return;

            vm.IsBusy = true;
            try
            {
                var monitor = vm.MonitorInfo;
                uint input = 0;

                // Retry a few times — monitor may not respond immediately
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (token.IsCancellationRequested) return;
                    input = await Task.Run(() => MonitorService.ReadCurrentInput(monitor), token);
                    if (input != 0) break;
                    await Task.Delay(300, token);
                }

                if (token.IsCancellationRequested) return;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    vm.CurrentInput = input;
                });
            }
            finally
            {
                vm.IsBusy = false;
            }
        }
    }

    private void OpenConfigure()
    {
        var configWindow = new ConfigWindow(Monitors.ToList());
        configWindow.Owner = Application.Current.MainWindow;
        if (configWindow.ShowDialog() == true)
        {
            // Reload after config change
            _ = RefreshAsync();
        }
    }

    private static void MergeConfig(List<MonitorInfo> monitors, List<MonitorConfig> configs)
    {
        foreach (var monitor in monitors)
        {
            var config = configs.Find(c =>
                string.Equals(c.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase));

            if (config != null)
            {
                if (!string.IsNullOrWhiteSpace(config.DisplayName))
                {
                    monitor.Name = config.DisplayName;
                }

                monitor.Inputs = config.Inputs
                    .Select(i => new InputEntry { Label = i.Label, VcpValue = i.VcpValue })
                    .ToList();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
