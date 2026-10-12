using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MonitorSwitcher.Core;

namespace MonitorSwitcher.Views;

public partial class IdentifyWindow : Window
{
    private readonly DispatcherTimer _timer;

    internal IdentifyWindow(string monitorNumber, string monitorName, NativeMethods.RECT bounds)
    {
        InitializeComponent();

        MonitorNumberText.Text = monitorNumber;
        MonitorNameText.Text = monitorName;

        // Move onto the target monitor in physical pixels, then maximize there (before the
        // first paint) so the overlay covers that monitor exactly whatever its DPI scaling.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                bounds.Left, bounds.Top, Math.Max(1, bounds.Width / 2), Math.Max(1, bounds.Height / 2),
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        };
        Loaded += (_, _) => WindowState = WindowState.Maximized;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => { _timer.Stop(); Close(); };
        _timer.Start();

        MouseDown += (_, _) => { _timer.Stop(); Close(); };
        KeyDown += (_, _) => { _timer.Stop(); Close(); };
    }
}
