# Copilot Instructions — MonitorSwitcher

A lightweight, portable .NET 8 WPF desktop application that lets the user switch input
sources on DDC/CI-capable monitors. No installer, no admin rights required.

---

## Project Setup

- **Framework:** .NET 8, WPF (`<UseWPF>true</UseWPF>`)
- **Target:** `win-x64`, self-contained single-file executable
- **Publish flags:**
  ```
  dotnet publish -c Release -r win-x64 --self-contained true
    /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
  ```
- **No NuGet packages required.** All monitor communication is done via Win32 P/Invoke.
- Output: a single `.exe`, ~70–90 MB, runs from any folder, no install, no admin rights.

---

## Solution Structure

```
MonitorSwitcher/
├── MonitorSwitcher.csproj
├── app.manifest                # DPI awareness (PerMonitorV2)
├── App.xaml
├── App.xaml.cs
├── MainWindow.xaml
├── MainWindow.xaml.cs
├── Core/
│   ├── MonitorInfo.cs          # Data model for a single monitor
│   ├── MonitorService.cs       # Enumerates monitors, reads/writes DDC/CI
│   ├── ConfigService.cs        # Load/save appsettings.json
│   └── NativeMethods.cs        # All P/Invoke declarations
├── ViewModels/
│   ├── MainViewModel.cs        # Top-level VM, owns ObservableCollection<MonitorViewModel>
│   ├── MonitorViewModel.cs     # Wraps MonitorInfo for binding
│   ├── InputEntryViewModel.cs  # Wraps InputEntry, exposes IsActive
│   └── RelayCommand.cs         # Minimal ICommand implementation
├── Views/
│   ├── ConfigWindow.xaml        # Monitor configuration dialog
│   └── ConfigWindow.xaml.cs
└── Converters/
    └── BoolToVisibilityConverter.cs
```

---

## Win32 P/Invoke Layer — `NativeMethods.cs`

Declare all interop in one static class. Use `using System.Runtime.InteropServices;`.

### Functions to declare

```csharp
// Enumerate display monitors
[DllImport("user32.dll")]
static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
    MonitorEnumProc lpfnEnum, IntPtr dwData);

delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor,
    ref RECT lprcMonitor, IntPtr dwData);

// Get monitor info (friendly name, handle)
[DllImport("user32.dll", CharSet = CharSet.Auto)]
static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

// Physical monitor DDC/CI handles
[DllImport("dxva2.dll")]
static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
    IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

[DllImport("dxva2.dll")]
static extern bool GetPhysicalMonitorsFromHMONITOR(
    IntPtr hMonitor, uint dwPhysicalMonitorArraySize,
    [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

[DllImport("dxva2.dll")]
static extern bool DestroyPhysicalMonitors(
    uint dwPhysicalMonitorArraySize,
    [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

// DDC/CI VCP get/set
[DllImport("dxva2.dll")]
static extern bool GetVCPFeatureAndVCPFeatureReply(
    IntPtr hPhysicalMonitor, byte bVCPCode,
    out MC_VCP_CODE_TYPE pvct,
    out uint pdwCurrentValue,
    out uint pdwMaximumValue);

[DllImport("dxva2.dll")]
static extern bool SetVCPFeature(
    IntPtr hPhysicalMonitor, byte bVCPCode, uint dwNewValue);
```

### Structs to declare

> **Important:** Before calling `GetMonitorInfo`, always initialize
> `info.cbSize = Marshal.SizeOf<MONITORINFOEX>()`. Forgetting this is a classic
> P/Invoke bug that causes the call to silently fail.

```csharp
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
struct MONITORINFOEX {
    public int cbSize;           // MUST be set to Marshal.SizeOf<MONITORINFOEX>() before use
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
struct PHYSICAL_MONITOR {
    public IntPtr hPhysicalMonitor;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szPhysicalMonitorDescription;
}

[StructLayout(LayoutKind.Sequential)]
struct RECT { public int Left, Top, Right, Bottom; }

enum MC_VCP_CODE_TYPE { MC_MOMENTARY, MC_SET_PARAMETER }
```

### VCP codes (byte constants)

```csharp
const byte VCP_INPUT_SOURCE = 0x60;   // Switch input source
const byte VCP_POWER_MODE   = 0xD6;   // Power control (not needed here, for reference)
```

### Common input source VCP values

These vary by manufacturer. Expose them as user-configurable (see MonitorInfo below),
but provide these defaults:

| Value | Typical meaning         |
|-------|-------------------------|
| 1     | VGA / Analog            |
| 3     | DVI                     |
| 4     | HDMI (some brands)      |
| 15    | DisplayPort             |
| 17    | HDMI-1 (other brands)   |
| 18    | HDMI-2                  |
| 27    | USB-C / DP alt          |

> **Note:** VCP 0x60 values are NOT standardized across manufacturers.
> The app must let users configure which values map to which label.

---

## Data Model — `MonitorInfo.cs`

```csharp
public class MonitorInfo
{
    // Display name shown in UI
    public string Name { get; set; }           // e.g. "Monitor 1 – DELL U2722D"

    // Windows logical monitor handle
    public IntPtr HMonitor { get; set; }

    // DDC/CI physical handle — opened lazily, closed after each operation
    public string DeviceName { get; set; }     // from MONITORINFOEX.szDevice

    // Current detected input (0 = unknown / DDC read failed)
    public uint CurrentInput { get; set; }

    // User-configured list of available inputs for this monitor
    public List<InputEntry> Inputs { get; set; } = new();
}

public class InputEntry
{
    public string Label { get; set; }   // e.g. "HDMI 1", "DisplayPort"
    public uint VcpValue { get; set; }  // e.g. 17
}
```

---

## Monitor Service — `MonitorService.cs`

All DDC/CI calls go through this service. Keep all P/Invoke usage isolated here.

### `GetMonitors()` → `List<MonitorInfo>`

1. Call `EnumDisplayMonitors` to collect all `hMonitor` handles.
2. For each handle, call `GetMonitorInfo` to get `szDevice` (e.g. `\\.\DISPLAY1`) and
   the monitor rectangle.
3. Build a `MonitorInfo` per handle. Name it `"Monitor {n}"` by default.
4. Do **not** open physical monitor handles yet (DDC is slow — open on demand).

### `ReadCurrentInput(MonitorInfo monitor)` → `uint`

1. Call `GetNumberOfPhysicalMonitorsFromHMONITOR` then `GetPhysicalMonitorsFromHMONITOR`
   to get the physical handle array.
2. Call `GetVCPFeatureAndVCPFeatureReply` with code `0x60` on the first physical handle.
3. Return `pdwCurrentValue`. Return `0` on any failure (DDC not supported, monitor
   asleep, etc.) — **never throw** from this method.
4. Always call `DestroyPhysicalMonitors` in a `finally` block.

### `SetInput(MonitorInfo monitor, uint vcpValue)` → `bool`

1. Same handle open/destroy pattern as above.
2. Call `SetVCPFeature(handle, 0x60, vcpValue)`.
3. After a successful call, wait ~100 ms then re-read with `ReadCurrentInput` to
   confirm the switch took effect. If the value didn't change, retry `SetVCPFeature`
   once. Do **not** retry more than once.
4. Return the bool result. Log failure via `Trace.WriteLine` — do not throw.
5. `DestroyPhysicalMonitors` in `finally`.

### Error handling contract

- DDC calls can fail silently (monitor off, cable type, firmware bugs).
- Wrap every Win32 call in try/catch. Never propagate exceptions to the UI.
- If a monitor doesn't support DDC, mark it in `MonitorInfo.CurrentInput = 0`
  and disable its input buttons in the UI.

### Logging

- Use `Trace.WriteLine` instead of `Debug.WriteLine` so logs are available in
  Release builds.
- Optionally add a `TextWriterTraceListener` that appends to
  `monitor-switcher.log` next to the exe for field diagnostics.

---

## Main Window — `MainWindow.xaml`

### Layout overview

```
┌─────────────────────────────────────────────┐
│  MonitorSwitcher                        [_][X]│
├─────────────────────────────────────────────┤
│  ┌─────────────────────────┐                 │
│  │  Monitor 1              │ [Refresh]        │
│  │  DELL U2722D            │                 │
│  │  Current input: HDMI 1  │                 │
│  │  [HDMI 1] [HDMI 2] [DP] │                 │
│  └─────────────────────────┘                 │
│  ┌─────────────────────────┐                 │
│  │  Monitor 2              │                 │
│  │  LG 27GP950             │                 │
│  │  Current input: unknown  │                 │
│  │  [HDMI 1] [HDMI 2] [DP] │                 │
│  └─────────────────────────┘                 │
│                              [Configure...]  │
└─────────────────────────────────────────────┘
```

### XAML implementation notes

- Window `ResizeMode="CanMinimize"` — fixed width (~420px), auto height.
- Use an `ItemsControl` bound to `ObservableCollection<MonitorViewModel>`.
- Each monitor card is a `Border` with `CornerRadius`, light drop shadow, margin.
- Input buttons: `ItemsControl` inside each card, bound to `MonitorInfo.Inputs`.
  Each button calls `SetInputCommand` with its `InputEntry`.
- The **active input button** should be highlighted (different background) when
  `InputEntry.VcpValue == MonitorInfo.CurrentInput`.
- Show `"Current input: {label}"` if known, `"Current input: unknown"` if `CurrentInput == 0`.
- **Refresh button**: re-reads DDC on a background thread, updates UI on dispatcher.
  Show a spinner (`ProgressRing` or animated ellipsis) while reading.
  If the user clicks Refresh while a previous read is in progress, cancel the
  in-flight operation via `CancellationTokenSource` before starting a new one.
  Refresh also re-enumerates monitors (handles plug/unplug).
- **Configure button**: opens a `ConfigWindow` (separate window, modal).

### Threading rule

All `MonitorService` calls are blocking Win32 calls. **Always run them on a
`Task.Run` background thread.** Update `ObservableCollection` and UI properties
only on the UI thread via `Application.Current.Dispatcher.Invoke`.

---

## Configure Window — `ConfigWindow.xaml`

Allows the user to set up the input list for each monitor.

```
┌─────────────────────────────────────────────┐
│  Configure Monitors                     [X]  │
├─────────────────────────────────────────────┤
│  Monitor 1                                   │
│  Name: [DELL U2722D          ]               │
│  Inputs:                                     │
│    Label      VCP Value   [Remove]           │
│   [HDMI 1  ] [17        ] [  ✕  ]           │
│   [HDMI 2  ] [18        ] [  ✕  ]           │
│   [DisplayP] [15        ] [  ✕  ]           │
│                           [+ Add Input]      │
├─────────────────────────────────────────────┤
│  Monitor 2                                   │
│  ... (same pattern)                          │
├─────────────────────────────────────────────┤
│                         [Cancel]  [Save]     │
└─────────────────────────────────────────────┘
```

- Edits are made on a **copy** of the config; only committed on Save.
- VCP Value field: numeric `TextBox` with input validation (numbers only, 0–255).
- On Save: write config to `appsettings.json` next to the exe (see Persistence below).

---

## Persistence — `appsettings.json`

Store configuration in a JSON file next to the executable. Use `System.Text.Json`.

```json
{
  "monitors": [
    {
      "deviceName": "\\\\.\\DISPLAY1",
      "displayName": "DELL U2722D",
      "inputs": [
        { "label": "HDMI 1", "vcpValue": 17 },
        { "label": "HDMI 2", "vcpValue": 18 },
        { "label": "DisplayPort", "vcpValue": 15 }
      ]
    },
    {
      "deviceName": "\\\\.\\DISPLAY2",
      "displayName": "LG 27GP950",
      "inputs": [
        { "label": "HDMI", "vcpValue": 4 },
        { "label": "DisplayPort", "vcpValue": 15 }
      ]
    }
  ]
}
```

- Match saved monitors to detected monitors by `deviceName`.
- If a monitor is detected but has no saved config, show it with an empty input list
  and prompt the user to configure it.
- Path: `Path.Combine(AppContext.BaseDirectory, "appsettings.json")`

---

## MVVM Pattern

Use a minimal MVVM approach — no MVVM framework needed.

- `MainViewModel` implements `INotifyPropertyChanged`.
- Commands implemented as `RelayCommand` (write a simple one with `Action` + `Func<bool>`).
- `ObservableCollection<MonitorViewModel>` — one per detected monitor.
- `MonitorViewModel` wraps `MonitorInfo` and exposes bindable properties:
  `DisplayName`, `CurrentInputLabel`, `Inputs`, `IsBusy`, `SetInputCommand`.
- `InputEntryViewModel` wraps `InputEntry` and exposes:
  - `Label` (string), `VcpValue` (uint)
  - `IsActive` (bool) — `true` when `VcpValue == parent MonitorViewModel.CurrentInput`.
    Updated whenever the parent's `CurrentInput` changes.
  This makes active-button highlighting a simple `DataTrigger` on `IsActive`.
- `MonitorViewModel.Inputs` is `ObservableCollection<InputEntryViewModel>`.
- `MainWindow.xaml.cs` only wires up `DataContext = new MainViewModel()` and handles
  window events. No logic in code-behind beyond that.

---

## Persistence Service — `ConfigService.cs`

Owns all `appsettings.json` I/O. Keeps `MonitorService` focused on DDC/CI only.

- `LoadConfig()` → `List<MonitorConfig>` — reads and deserializes the JSON file.
- `SaveConfig(List<MonitorConfig> configs)` — serializes and writes the JSON file.
- Path: `Path.Combine(AppContext.BaseDirectory, "appsettings.json")`
- Uses `System.Text.Json` with `JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }`.

---

## Startup Behavior

1. On load, call `MonitorService.GetMonitors()` (synchronous is fine at startup).
2. Load `appsettings.json` via `ConfigService` and merge saved config into monitor list.
3. For each monitor, attempt to read current input via DDC on a background thread.
4. Show the window — cards appear immediately, input states update as DDC reads complete.
5. **First run (no `appsettings.json`):** detect all monitors and open the
   `ConfigWindow` automatically so the user can set up inputs.

---

## Important Caveats to Handle Gracefully

| Situation | Behavior |
|-----------|----------|
| Monitor doesn't support DDC/CI | Disable input buttons, show "DDC not supported" |
| DDC read times out / returns 0 | Show "Current input: unknown", buttons still work |
| Physical handle open fails | Log to debug, show error state in card |
| Monitor list changes (plugged/unplugged) | Refresh button re-enumerates |
| Two physical monitors per hMonitor | Handle `GetNumberOfPhysicalMonitors > 1` — iterate all physical handles |

---

## What NOT to Build

- No system tray — plain window only.
- No hotkeys (out of scope for this version).
- No auto-window-moving between monitors (not reliable via WinAPI for the general case).
- No power off / monitor reset (out of scope per requirements).
- No elevation / UAC — must work without admin rights.
- No auto-launch on startup (out of scope).

---

## Key Gotchas

1. **`dxva2.dll` is available on all modern Windows without admin rights.** No elevation needed.
2. **Physical monitor handles must be destroyed after every use.** Leaking them causes DDC to stop responding.
3. **DDC reads can take 200–500ms per monitor.** Always async / background thread.
4. **VCP 0x60 values differ per brand.** Never hardcode; always user-configurable.
5. **`GetVCPFeatureAndVCPFeatureReply` may return `true` but wrong values** if the monitor is off or in standby. Treat any value of 0 as "unknown."
6. **Single-file publish extracts to a temp folder on first run.** `AppContext.BaseDirectory` points to the *original* exe location, which is correct for `appsettings.json`. Do not use `Assembly.Location` (it points to the temp extraction folder).
7. **WPF DPI awareness:** Add this to the `.csproj`:
   ```xml
   <ApplicationManifest>app.manifest</ApplicationManifest>
   ```
   And in `app.manifest`, include this content to enable per-monitor DPI awareness (v2):
   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
     <application xmlns="urn:schemas-microsoft-com:asm.v3">
       <windowsSettings>
         <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
         <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
       </windowsSettings>
     </application>
   </assembly>
   ```
8. **Multiple physical monitors per hMonitor:** When `GetNumberOfPhysicalMonitors` returns > 1, create one `MonitorInfo` per physical monitor, not per logical `hMonitor`. Iterate all physical handles in the returned array.
