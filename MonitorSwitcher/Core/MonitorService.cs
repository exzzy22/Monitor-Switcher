using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using static MonitorSwitcher.Core.NativeMethods;

namespace MonitorSwitcher.Core;

public enum DdcStatus
{
    Ok,
    /// <summary>Windows doesn't list the monitor right now (unplugged, or showing another source).</summary>
    NotConnected,
    /// <summary>Windows has no DDC/CI handle for the monitor.</summary>
    NoDdc,
    /// <summary>The monitor didn't answer or rejected the command.</summary>
    Failed,
    /// <summary>Another command to the same monitor didn't finish in time.</summary>
    Busy,
}

public static class MonitorService
{
    // DDC/CI hosts must leave a short pause between messages to the same display.
    private const int MinCommandGapMs = 50;
    private const int ReadLockTimeoutMs = 2000;
    private const int SetLockTimeoutMs = 5000;
    private const int CapabilitiesLockTimeoutMs = 8000;

    private static readonly int[] SetRetryDelaysMs = { 0, 250, 600 };

    private static readonly ConcurrentDictionary<string, MonitorChannel> Channels =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Serializes DDC/CI traffic per monitor. Overlapping reads and writes on the same
    /// I2C bus corrupt each other, which made input switches randomly fail.
    /// </summary>
    private sealed class MonitorChannel
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public DateTime LastCommandUtc = DateTime.MinValue;
    }

    private sealed class DisplayTarget
    {
        public string DevicePath = string.Empty;
        public string FriendlyName = string.Empty;
    }

    private sealed class Display
    {
        public IntPtr HMonitor;
        public string DeviceName = string.Empty;
        public RECT Bounds;
        public List<DisplayTarget> Targets = new();
    }

    // ---------------------------------------------------------------- enumeration

    /// <summary>Lists connected monitors, ordered left to right as they're arranged in Windows.</summary>
    public static List<MonitorInfo> GetMonitors()
    {
        var found = new List<(MonitorInfo Info, RECT Bounds, string FriendlyName)>();

        foreach (var display in EnumerateDisplays())
        {
            uint physicalCount = 0;
            try
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(display.HMonitor, out physicalCount))
                    physicalCount = 0;
            }
            catch (Exception ex)
            {
                Log.Error($"GetNumberOfPhysicalMonitorsFromHMONITOR failed for {display.DeviceName}", ex);
            }

            int count = Math.Max(1, (int)physicalCount);
            for (int i = 0; i < count; i++)
            {
                var target = i < display.Targets.Count ? display.Targets[i] : null;
                string friendlyName = !string.IsNullOrWhiteSpace(target?.FriendlyName)
                    ? target!.FriendlyName
                    : i == 0 ? GetDriverMonitorName(display.DeviceName) : string.Empty;

                string id = !string.IsNullOrEmpty(target?.DevicePath)
                    ? target!.DevicePath
                    : $"{display.DeviceName}#{i}";

                found.Add((new MonitorInfo
                {
                    Id = id,
                    DeviceName = display.DeviceName,
                    PhysicalIndex = i,
                }, display.Bounds, friendlyName));
            }
        }

        found.Sort((a, b) =>
            a.Bounds.Left != b.Bounds.Left ? a.Bounds.Left.CompareTo(b.Bounds.Left)
            : a.Bounds.Top != b.Bounds.Top ? a.Bounds.Top.CompareTo(b.Bounds.Top)
            : a.Info.PhysicalIndex.CompareTo(b.Info.PhysicalIndex));

        for (int i = 0; i < found.Count; i++)
        {
            found[i].Info.Name = string.IsNullOrWhiteSpace(found[i].FriendlyName)
                ? $"Monitor {i + 1}"
                : $"Monitor {i + 1} – {found[i].FriendlyName}";
        }

        return found.Select(f => f.Info).ToList();
    }

    /// <summary>
    /// True when <paramref name="id"/> is a Windows monitor device path, which stays the same
    /// across reconnects (as opposed to the "\\.\DISPLAYn#i" fallback).
    /// </summary>
    public static bool IsStableId(string? id) =>
        id != null && id.StartsWith(@"\\?\", StringComparison.Ordinal);

    /// <summary>
    /// The make/model part of a monitor device path (e.g. "DELA1F3"), used to recognise the
    /// same monitor after its cable was moved to another port.
    /// </summary>
    public static string GetHardwareId(string? id)
    {
        if (!IsStableId(id))
            return string.Empty;

        var parts = id!.Split('#');
        return parts.Length > 2 ? parts[1] : string.Empty;
    }

    private static List<Display> EnumerateDisplays()
    {
        var targetsBySource = QueryDisplayTargets();
        var displays = new List<Display>();

        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            try
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    string deviceName = info.szDevice ?? string.Empty;
                    displays.Add(new Display
                    {
                        HMonitor = hMonitor,
                        DeviceName = deviceName,
                        Bounds = info.rcMonitor,
                        Targets = targetsBySource.TryGetValue(deviceName, out var targets)
                            ? targets
                            : new List<DisplayTarget>(),
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error("EnumDisplayMonitors callback failed", ex);
            }
            return true;
        };

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            Log.Warn("EnumDisplayMonitors failed");
        GC.KeepAlive(callback);

        return displays;
    }

    /// <summary>
    /// Maps each GDI display name (\\.\DISPLAY1) to the monitors driven by it, with their
    /// device paths and EDID names (e.g. "DELL U2722D").
    /// </summary>
    private static Dictionary<string, List<DisplayTarget>> QueryDisplayTargets()
    {
        var map = new Dictionary<string, List<DisplayTarget>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            DISPLAYCONFIG_PATH_INFO[] paths;
            uint pathCount;
            int status;
            int attempts = 0;
            do
            {
                // The topology can change between the two calls; then the buffers are too small.
                status = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out uint modeCount);
                if (status != ERROR_SUCCESS)
                    break;

                paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                status = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (status == ERROR_SUCCESS)
                {
                    for (int i = 0; i < pathCount; i++)
                        AddTarget(map, paths[i]);
                    return map;
                }
            }
            while (status == ERROR_INSUFFICIENT_BUFFER && ++attempts < 5);

            Log.Warn($"QueryDisplayConfig failed with error {status}");
        }
        catch (Exception ex)
        {
            Log.Error("QueryDisplayConfig failed", ex);
        }
        return map;
    }

    private static void AddTarget(Dictionary<string, List<DisplayTarget>> map, DISPLAYCONFIG_PATH_INFO path)
    {
        var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
        source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        source.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
        source.header.adapterId = path.sourceInfo.adapterId;
        source.header.id = path.sourceInfo.id;
        if (DisplayConfigGetDeviceInfo(ref source) != ERROR_SUCCESS || string.IsNullOrEmpty(source.viewGdiDeviceName))
            return;

        var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        target.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        target.header.adapterId = path.targetInfo.adapterId;
        target.header.id = path.targetInfo.id;
        if (DisplayConfigGetDeviceInfo(ref target) != ERROR_SUCCESS)
            return;

        if (!map.TryGetValue(source.viewGdiDeviceName, out var list))
            map[source.viewGdiDeviceName] = list = new List<DisplayTarget>();

        list.Add(new DisplayTarget
        {
            DevicePath = target.monitorDevicePath ?? string.Empty,
            FriendlyName = target.monitorFriendlyDeviceName ?? string.Empty,
        });
    }

    /// <summary>
    /// Fallback name from the monitor driver (e.g. "Dell U2722D") when the EDID has none.
    /// </summary>
    private static string GetDriverMonitorName(string adapterDeviceName)
    {
        try
        {
            var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint i = 0; EnumDisplayDevices(adapterDeviceName, i, ref device, 0); i++)
            {
                if (!string.IsNullOrWhiteSpace(device.DeviceString)
                    && !device.DeviceString.Equals("Generic PnP Monitor", StringComparison.OrdinalIgnoreCase))
                {
                    return device.DeviceString;
                }
                device.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            }
        }
        catch (Exception ex)
        {
            Log.Error("EnumDisplayDevices failed", ex);
        }
        return string.Empty;
    }

    /// <summary>
    /// Finds the monitor's current HMONITOR. Handles go stale whenever Windows re-enumerates
    /// displays (e.g. after another monitor switches away), so never cache them.
    /// </summary>
    private static Display? FindDisplay(MonitorInfo monitor, out int physicalIndex)
    {
        var displays = EnumerateDisplays();

        if (IsStableId(monitor.Id))
        {
            foreach (var display in displays)
            {
                int index = display.Targets.FindIndex(t =>
                    string.Equals(t.DevicePath, monitor.Id, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    physicalIndex = index;
                    return display;
                }
            }

            // A display name can point at a different monitor after Windows renumbers displays,
            // so only fall back to it if monitor ids aren't available at all right now.
            if (displays.Any(d => d.Targets.Count > 0))
            {
                physicalIndex = 0;
                return null;
            }
        }

        physicalIndex = monitor.PhysicalIndex;
        return displays.Find(d => string.Equals(d.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryGetMonitorBounds(MonitorInfo monitor, out RECT bounds)
    {
        var display = FindDisplay(monitor, out _);
        bounds = display?.Bounds ?? default;
        return display != null;
    }

    // ---------------------------------------------------------------- DDC/CI

    private static DdcStatus WithPhysicalMonitor(MonitorInfo monitor, int lockTimeoutMs, Func<IntPtr, bool> action)
    {
        var channel = Channels.GetOrAdd(monitor.Id, _ => new MonitorChannel());
        if (!channel.Gate.Wait(lockTimeoutMs))
            return DdcStatus.Busy;

        try
        {
            var display = FindDisplay(monitor, out int physicalIndex);
            if (display == null)
                return DdcStatus.NotConnected;

            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(display.HMonitor, out uint count) || count == 0)
                return DdcStatus.NoDdc;

            var physicalMonitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(display.HMonitor, count, physicalMonitors))
                return DdcStatus.NoDdc;

            try
            {
                var handle = physicalMonitors[Math.Min(physicalIndex, physicalMonitors.Length - 1)].hPhysicalMonitor;

                var wait = channel.LastCommandUtc.AddMilliseconds(MinCommandGapMs) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    Thread.Sleep(wait);

                try
                {
                    return action(handle) ? DdcStatus.Ok : DdcStatus.Failed;
                }
                finally
                {
                    channel.LastCommandUtc = DateTime.UtcNow;
                }
            }
            finally
            {
                DestroyPhysicalMonitors(count, physicalMonitors);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"DDC/CI call failed for {monitor.Name}", ex);
            return DdcStatus.Failed;
        }
        finally
        {
            channel.Gate.Release();
        }
    }

    /// <summary>Returns the current input's VCP value, or 0 if it couldn't be read.</summary>
    public static uint ReadCurrentInput(MonitorInfo monitor)
    {
        uint value = 0;
        WithPhysicalMonitor(monitor, ReadLockTimeoutMs, handle =>
        {
            if (!GetVCPFeatureAndVCPFeatureReply(handle, VCP_INPUT_SOURCE, out _, out uint current, out _))
                return false;

            // Input source is a single-byte value; some monitors put junk in the high byte.
            value = current & 0xFF;
            return true;
        });
        return value;
    }

    public static DdcStatus SetInput(MonitorInfo monitor, uint vcpValue)
    {
        var status = DdcStatus.Failed;
        bool commandReachedMonitor = false;

        for (int attempt = 0; attempt < SetRetryDelaysMs.Length; attempt++)
        {
            if (SetRetryDelaysMs[attempt] > 0)
                Thread.Sleep(SetRetryDelaysMs[attempt]);

            status = WithPhysicalMonitor(monitor, SetLockTimeoutMs,
                handle => SetVCPFeature(handle, VCP_INPUT_SOURCE, vcpValue));

            if (status == DdcStatus.Ok)
            {
                Log.Info($"Switched {monitor.Name} to VCP {vcpValue}" + (attempt > 0 ? $" (attempt {attempt + 1})" : ""));
                return status;
            }

            if (status == DdcStatus.NotConnected && commandReachedMonitor)
            {
                // The monitor dropped off right after the previous attempt, which is what a
                // DisplayPort monitor does once it has switched to another source.
                Log.Info($"{monitor.Name} disconnected after the switch command to VCP {vcpValue}; assuming it switched");
                return DdcStatus.Ok;
            }

            Log.Warn($"Switching {monitor.Name} to VCP {vcpValue} failed: {status} (attempt {attempt + 1})");

            // Retry when the command wasn't acknowledged, or when the monitor briefly vanished
            // while Windows re-arranges displays (e.g. another monitor just switched away).
            if (status == DdcStatus.Failed)
                commandReachedMonitor = true;
            else if (status != DdcStatus.NotConnected)
                return status;
        }

        return status;
    }

    /// <summary>
    /// Reads the DDC/CI capabilities string and returns the VCP 0x60 values it lists.
    /// </summary>
    public static DdcStatus DetectSupportedInputs(MonitorInfo monitor, out List<uint> values)
    {
        var result = new List<uint>();
        var status = DdcStatus.Failed;

        // Capability requests are long multi-part transfers and often fail the first time.
        for (int attempt = 0; attempt < 2 && status != DdcStatus.Ok; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(300);

            status = WithPhysicalMonitor(monitor, CapabilitiesLockTimeoutMs, handle =>
            {
                if (!GetCapabilitiesStringLength(handle, out uint length) || length == 0)
                    return false;

                var buffer = new byte[length];
                if (!CapabilitiesRequestAndCapabilitiesReply(handle, buffer, length))
                    return false;

                string capabilities = Encoding.ASCII.GetString(buffer).TrimEnd('\0');
                Log.Info($"Capabilities for {monitor.Name}: {capabilities}");
                result = ParseInputValuesFromCapabilities(capabilities);
                return true;
            });

            if (status is DdcStatus.NotConnected or DdcStatus.NoDdc)
                break;
        }

        values = result;
        return status;
    }

    /// <summary>
    /// Parses a DDC/CI capabilities string to extract VCP 0x60 supported values.
    /// Example: "...vcp(10 12 60(01 03 04 0F 11 12) AC AE)..." returns [1, 3, 4, 15, 17, 18].
    /// </summary>
    internal static List<uint> ParseInputValuesFromCapabilities(string capabilities)
    {
        var result = new List<uint>();

        // "60(" not preceded by another hex digit, so codes like "E60(" don't match.
        var match = Regex.Match(capabilities, @"(?<![0-9A-F])60\s*\(([^)]+)\)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return result;

        foreach (var token in match.Groups[1].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            // Some monitors omit the spaces ("0F1112"), so split long tokens into byte pairs.
            var hexValues = token.Length > 2 && token.Length % 2 == 0
                ? Enumerable.Range(0, token.Length / 2).Select(i => token.Substring(i * 2, 2))
                : new[] { token };

            foreach (var hex in hexValues)
            {
                if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint value)
                    && !result.Contains(value))
                {
                    result.Add(value);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Where to insert a newly detected input so the list keeps the order the monitor reports:
    /// before the first listed input the monitor reports after it, otherwise at the end.
    /// </summary>
    public static int GetDetectedInputIndex(List<uint> listed, List<uint> detected, uint value)
    {
        int position = detected.IndexOf(value);
        int index = listed.FindIndex(v => detected.IndexOf(v) > position);
        return index < 0 ? listed.Count : index;
    }

    /// <summary>
    /// Known MCCS standard labels for VCP 0x60 values.
    /// Monitors may use these differently, but it's a reasonable default label.
    /// </summary>
    private static readonly Dictionary<uint, string> KnownInputLabels = new()
    {
        [1] = "VGA-1",
        [2] = "VGA-2",
        [3] = "DVI-1",
        [4] = "DVI-2",
        [5] = "Composite-1",
        [6] = "Composite-2",
        [7] = "S-Video-1",
        [8] = "S-Video-2",
        [9] = "Tuner-1",
        [10] = "Tuner-2",
        [11] = "Component-1",
        [12] = "Component-2",
        [13] = "Component-3",
        [14] = "DisplayPort-3",
        [15] = "DisplayPort-1",
        [16] = "DisplayPort-2",
        [17] = "HDMI-1",
        [18] = "HDMI-2",
        [27] = "USB-C / DP Alt",
    };

    public static string GetDefaultLabelForVcpValue(uint vcpValue)
    {
        return KnownInputLabels.TryGetValue(vcpValue, out string? label)
            ? label
            : $"Input {vcpValue}";
    }
}
