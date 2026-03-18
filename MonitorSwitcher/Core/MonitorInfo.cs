namespace MonitorSwitcher.Core;

public class MonitorInfo
{
    public string Name { get; set; } = string.Empty;
    public IntPtr HMonitor { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public uint CurrentInput { get; set; }
    public List<InputEntry> Inputs { get; set; } = new();
    public bool IsDisabled { get; set; }
}

public class InputEntry
{
    public string Label { get; set; } = string.Empty;
    public uint VcpValue { get; set; }
}
