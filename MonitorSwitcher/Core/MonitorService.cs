using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using static MonitorSwitcher.Core.NativeMethods;

namespace MonitorSwitcher.Core;

public static class MonitorService
{
    public static List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        int index = 1;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
        {
            try
            {
                var info = new MONITORINFOEX();
                info.cbSize = Marshal.SizeOf<MONITORINFOEX>();

                if (GetMonitorInfo(hMonitor, ref info))
                {
                    string deviceName = info.szDevice ?? string.Empty;
                    string friendlyName = GetFriendlyMonitorName(deviceName);

                    // Check for multiple physical monitors per logical monitor
                    if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint physCount) && physCount > 0)
                    {
                        var physMonitors = new PHYSICAL_MONITOR[physCount];
                        if (GetPhysicalMonitorsFromHMONITOR(hMonitor, physCount, physMonitors))
                        {
                            for (int i = 0; i < physCount; i++)
                            {
                                string name = !string.IsNullOrWhiteSpace(friendlyName)
                                    ? $"Monitor {index} – {friendlyName}"
                                    : $"Monitor {index}";

                                monitors.Add(new MonitorInfo
                                {
                                    Name = name,
                                    HMonitor = hMonitor,
                                    DeviceName = deviceName,
                                });
                                index++;
                            }

                            DestroyPhysicalMonitors(physCount, physMonitors);
                        }
                        else
                        {
                            monitors.Add(new MonitorInfo
                            {
                                Name = !string.IsNullOrWhiteSpace(friendlyName)
                                    ? $"Monitor {index} – {friendlyName}"
                                    : $"Monitor {index}",
                                HMonitor = hMonitor,
                                DeviceName = deviceName,
                            });
                            index++;
                        }
                    }
                    else
                    {
                        monitors.Add(new MonitorInfo
                        {
                            Name = !string.IsNullOrWhiteSpace(friendlyName)
                                ? $"Monitor {index} – {friendlyName}"
                                : $"Monitor {index}",
                            HMonitor = hMonitor,
                            DeviceName = deviceName,
                        });
                        index++;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"MonitorService.GetMonitors enumerate error: {ex.Message}");
            }

            return true;
        }, IntPtr.Zero);

        return monitors;
    }

    /// <summary>
    /// Uses EnumDisplayDevices to get the real monitor name (e.g. "DELL U2722D")
    /// instead of "Generic PnP Monitor" from the physical monitor description.
    /// </summary>
    private static string GetFriendlyMonitorName(string adapterDeviceName)
    {
        try
        {
            // EnumDisplayDevices with the adapter name + index 0 gives us the monitor
            var displayDevice = new DISPLAY_DEVICE();
            displayDevice.cb = Marshal.SizeOf<DISPLAY_DEVICE>();

            for (uint i = 0; EnumDisplayDevices(adapterDeviceName, i, ref displayDevice, 0); i++)
            {
                // DeviceString contains the friendly name like "DELL U2722D"
                if (!string.IsNullOrWhiteSpace(displayDevice.DeviceString)
                    && !displayDevice.DeviceString.Equals("Generic PnP Monitor", StringComparison.OrdinalIgnoreCase))
                {
                    return displayDevice.DeviceString;
                }

                displayDevice.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            }

            // Fall back — return whatever DeviceString we got, even if generic
            displayDevice.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            if (EnumDisplayDevices(adapterDeviceName, 0, ref displayDevice, 0))
            {
                return displayDevice.DeviceString ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"MonitorService.GetFriendlyMonitorName error: {ex.Message}");
        }

        return string.Empty;
    }

    public static uint ReadCurrentInput(MonitorInfo monitor)
    {
        PHYSICAL_MONITOR[]? physMonitors = null;
        try
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor.HMonitor, out uint count) || count == 0)
                return 0;

            physMonitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor.HMonitor, count, physMonitors))
                return 0;

            if (GetVCPFeatureAndVCPFeatureReply(
                    physMonitors[0].hPhysicalMonitor,
                    VCP_INPUT_SOURCE,
                    out _,
                    out uint currentValue,
                    out _))
            {
                return currentValue;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"MonitorService.ReadCurrentInput error: {ex.Message}");
            return 0;
        }
        finally
        {
            if (physMonitors != null)
            {
                DestroyPhysicalMonitors((uint)physMonitors.Length, physMonitors);
            }
        }
    }

    public static bool SetInput(MonitorInfo monitor, uint vcpValue)
    {
        PHYSICAL_MONITOR[]? physMonitors = null;
        try
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor.HMonitor, out uint count) || count == 0)
                return false;

            physMonitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor.HMonitor, count, physMonitors))
                return false;

            bool result = SetVCPFeature(physMonitors[0].hPhysicalMonitor, VCP_INPUT_SOURCE, vcpValue);

            if (result)
            {
                // Wait and verify
                Thread.Sleep(100);

                // Re-read to confirm
                uint current = ReadCurrentInputFromHandle(physMonitors[0].hPhysicalMonitor);
                if (current != vcpValue)
                {
                    // Retry once
                    Trace.WriteLine($"MonitorService.SetInput: first attempt did not take effect, retrying.");
                    result = SetVCPFeature(physMonitors[0].hPhysicalMonitor, VCP_INPUT_SOURCE, vcpValue);
                }
            }

            if (!result)
            {
                Trace.WriteLine($"MonitorService.SetInput failed for {monitor.DeviceName} value={vcpValue}");
            }

            return result;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"MonitorService.SetInput error: {ex.Message}");
            return false;
        }
        finally
        {
            if (physMonitors != null)
            {
                DestroyPhysicalMonitors((uint)physMonitors.Length, physMonitors);
            }
        }
    }

    private static uint ReadCurrentInputFromHandle(IntPtr hPhysicalMonitor)
    {
        try
        {
            if (GetVCPFeatureAndVCPFeatureReply(
                    hPhysicalMonitor,
                    VCP_INPUT_SOURCE,
                    out _,
                    out uint currentValue,
                    out _))
            {
                return currentValue;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"MonitorService.ReadCurrentInputFromHandle error: {ex.Message}");
        }

        return 0;
    }

    /// <summary>
    /// Reads the DDC/CI capabilities string and parses the supported VCP 0x60 values.
    /// Returns a list of VCP values that the monitor reports it supports for input switching.
    /// </summary>
    public static List<uint> DetectSupportedInputs(MonitorInfo monitor)
    {
        PHYSICAL_MONITOR[]? physMonitors = null;
        try
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor.HMonitor, out uint count) || count == 0)
                return new List<uint>();

            physMonitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor.HMonitor, count, physMonitors))
                return new List<uint>();

            var handle = physMonitors[0].hPhysicalMonitor;

            if (!GetCapabilitiesStringLength(handle, out uint len) || len == 0)
                return new List<uint>();

            var buffer = new byte[len];
            if (!CapabilitiesRequestAndCapabilitiesReply(handle, buffer, len))
                return new List<uint>();

            string caps = Encoding.ASCII.GetString(buffer).TrimEnd('\0');
            Trace.WriteLine($"MonitorService.DetectSupportedInputs caps for {monitor.DeviceName}: {caps}");

            return ParseInputValuesFromCapabilities(caps);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"MonitorService.DetectSupportedInputs error: {ex.Message}");
            return new List<uint>();
        }
        finally
        {
            if (physMonitors != null)
            {
                DestroyPhysicalMonitors((uint)physMonitors.Length, physMonitors);
            }
        }
    }

    /// <summary>
    /// Parses a DDC/CI capabilities string to extract VCP 0x60 supported values.
    /// Example: "...vcp(10 12 60(01 03 04 0F 11 12) AC AE)..."
    /// Returns [1, 3, 4, 15, 17, 18]
    /// </summary>
    internal static List<uint> ParseInputValuesFromCapabilities(string capabilities)
    {
        var result = new List<uint>();

        // Match "60(" followed by hex values and ")"
        // The pattern handles both "60(xx xx xx)" and "60 (xx xx xx)"
        var match = Regex.Match(capabilities, @"60\s*\(([^)]+)\)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return result;

        string valuesStr = match.Groups[1].Value;
        var hexValues = valuesStr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var hex in hexValues)
        {
            if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint val))
            {
                result.Add(val);
            }
        }

        return result;
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
