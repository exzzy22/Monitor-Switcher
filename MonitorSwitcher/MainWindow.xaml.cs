using System.Windows;
using System.Windows.Interop;
using MonitorSwitcher.ViewModels;

namespace MonitorSwitcher;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private HwndSource? _hwndSource;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        Loaded += async (_, _) =>
        {
            await _viewModel.InitializeAsync();
        };

        Activated += async (_, _) =>
        {
            if (_viewModel != null && !_viewModel.IsRefreshing)
            {
                await _viewModel.SilentReadInputsAsync();
            }
        };

        SourceInitialized += (_, _) =>
        {
            _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwndSource?.AddHook(WndProc);
        };

        Closed += (_, _) =>
        {
            _hwndSource?.RemoveHook(WndProc);
            _hwndSource = null;

            _viewModel?.Dispose();
            _viewModel = null;
        };
    }

    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_ACTIVATE = 1;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return (IntPtr)MA_ACTIVATE;
        }
        return IntPtr.Zero;
    }
}
