using System.Windows;
using System.Windows.Threading;
using MonitorSwitcher.Core;

namespace MonitorSwitcher;

public partial class App : Application
{
    private const string InstanceName = @"Local\MonitorSwitcher-8F2C6A41";

    // Held for the app's lifetime; if it were collected, a second copy could start.
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showRequest;
    private EventWaitHandle? _showAck;

    static App()
    {
        // Let WPF rescale when a window moves between monitors with different DPI
        // (the manifest declares PerMonitorV2).
        AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);

        if (!AcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        Log.Info($"Starting MonitorSwitcher {typeof(App).Assembly.GetName().Version}");

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        ListenForShowRequests(window);
    }

    /// <summary>
    /// Returns false when another copy is already running and has been brought to the front.
    /// Two copies talking DDC/CI to the same monitors make switching unreliable.
    /// </summary>
    private bool AcquireSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceName, out bool createdNew);
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "-show");
        _showAck = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "-ack");

        if (createdNew)
            return true;

        // We're the foreground process; let the running copy take focus.
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        _showRequest.Set();

        if (_showAck.WaitOne(TimeSpan.FromSeconds(3)))
            return false;

        Log.Warn("Another instance is running but didn't respond; starting anyway.");
        return true;
    }

    private void ListenForShowRequests(MainWindow window)
    {
        var showRequest = _showRequest!;
        var showAck = _showAck!;

        var thread = new Thread(() =>
        {
            while (showRequest.WaitOne())
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    window.BringToFront();
                    showAck.Set();
                }));
            }
        })
        {
            IsBackground = true,
            Name = "Single instance listener",
        };
        thread.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to:\n{Log.FilePath}",
            "Monitor Input Switcher", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
