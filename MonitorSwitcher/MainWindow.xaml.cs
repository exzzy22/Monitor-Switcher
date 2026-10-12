using System.Windows;
using System.Windows.Interop;
using MonitorSwitcher.Core;
using MonitorSwitcher.ViewModels;
using static MonitorSwitcher.Core.NativeMethods;

namespace MonitorSwitcher;

public partial class MainWindow : Window
{
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_ACTIVATE = 1;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    private MainViewModel? _viewModel;
    private HwndSource? _hwndSource;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        Loaded += async (_, _) =>
        {
            try
            {
                await _viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Initialization failed", ex);
            }
        };

        Activated += (_, _) => _viewModel?.OnWindowActivated();

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

    /// <summary>
    /// Shows the window on the monitor the user is looking at; used when the app is launched
    /// again while already running (the window may be on a monitor showing another device).
    /// </summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Show();
        MoveToMonitorUnderCursor();
        Activate();
    }

    private void MoveToMonitorUnderCursor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetCursorPos(out var cursor) || !GetWindowRect(hwnd, out var window))
            return;

        var cursorMonitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        if (cursorMonitor == MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL))
            return;

        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(cursorMonitor, ref info))
            return;

        var work = info.rcWork;
        int x = work.Left + Math.Max(0, (work.Width - window.Width) / 2);
        int y = work.Top + Math.Max(0, (work.Height - window.Height) / 2);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                handled = true;
                return (IntPtr)MA_ACTIVATE;

            // Monitors were added/removed or rearranged (e.g. one switched to another source).
            case WM_DISPLAYCHANGE:
                _viewModel?.OnDisplaySettingsChanged();
                break;

            case WM_POWERBROADCAST when wParam.ToInt64() == PBT_APMRESUMEAUTOMATIC:
                _viewModel?.OnDisplaySettingsChanged();
                break;
        }
        return IntPtr.Zero;
    }
}
