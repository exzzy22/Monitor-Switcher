using System.Windows;
using System.Windows.Threading;

namespace MonitorSwitcher.Views;

public partial class IdentifyWindow : Window
{
    private readonly DispatcherTimer _timer;

    public IdentifyWindow(string monitorNumber, string monitorName,
        double left, double top, double width, double height)
    {
        InitializeComponent();

        MonitorNumberText.Text = monitorNumber;
        MonitorNameText.Text = monitorName;

        Left = left;
        Top = top;
        Width = width;
        Height = height;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => { _timer.Stop(); Close(); };
        _timer.Start();

        MouseDown += (_, _) => { _timer.Stop(); Close(); };
        KeyDown += (_, _) => { _timer.Stop(); Close(); };
    }
}
