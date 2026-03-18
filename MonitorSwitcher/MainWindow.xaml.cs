using System.Windows;
using System.Windows.Interop;
using MonitorSwitcher.ViewModels;

namespace MonitorSwitcher;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        Loaded += async (_, _) =>
        {
            await _viewModel.InitializeAsync();
        };

        // Re-read actual input states when the window is reactivated
        // (e.g. user switched monitor away and came back)
        Activated += async (_, _) =>
        {
            if (_viewModel != null && !_viewModel.IsRefreshing)
            {
                await _viewModel.SilentReadInputsAsync();
            }
        };

        SourceInitialized += (_, _) =>
        {
            // Hook WM_MOUSEACTIVATE so button clicks work without needing
            // a separate click to focus the window first.
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WndProc);
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
