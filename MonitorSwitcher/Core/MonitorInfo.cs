namespace MonitorSwitcher.Core;

public class MonitorInfo
{
    /// <summary>
    /// Stable identity: the monitor's device path (e.g. <c>\\?\DISPLAY#DELA1F3#...</c>), which
    /// survives Windows renumbering displays. Falls back to "DeviceName#index" when unavailable.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>GDI display name, e.g. <c>\\.\DISPLAY1</c>. Can change when monitors reconnect.</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Index among the physical monitors behind one display (more than one when duplicating).</summary>
    public int PhysicalIndex { get; set; }

    /// <summary>False when Windows no longer lists the monitor, e.g. it switched to another source.</summary>
    public bool IsConnected { get; set; } = true;

    public uint CurrentInput { get; set; }
    public List<InputEntry> Inputs { get; set; } = new();

    /// <summary>VCP values the user removed; automatic detection skips them.</summary>
    public List<uint> RemovedInputs { get; set; } = new();

    public bool IsDisabled { get; set; }

    public MonitorInfo CloneAsDisconnected() => new()
    {
        Id = Id,
        Name = Name,
        DeviceName = DeviceName,
        PhysicalIndex = PhysicalIndex,
        IsConnected = false,
        CurrentInput = CurrentInput,
        Inputs = Inputs.Select(i => new InputEntry { Label = i.Label, VcpValue = i.VcpValue }).ToList(),
        RemovedInputs = RemovedInputs.ToList(),
        IsDisabled = IsDisabled,
    };
}

public class InputEntry
{
    public string Label { get; set; } = string.Empty;
    public uint VcpValue { get; set; }
}
