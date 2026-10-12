# Copilot Instructions — MonitorSwitcher

A lightweight, portable .NET Framework 4.8 WPF desktop application that lets the user switch
input sources on DDC/CI-capable monitors. No installer, no admin rights required.

---

## Project Setup

- **Framework:** .NET Framework 4.8 (`net48`), WPF (`<UseWPF>true</UseWPF>`), SDK-style project
  with `<LangVersion>latest</LangVersion>`.
- **Why not .NET 10:** a self-contained single-file .NET 10 WPF exe must unpack native WPF DLLs
  to `%TEMP%\.net` on every launch. When Storage Sense or a temp cleaner interfered, the host
  aborted before any app code ran ("Failed to determine location for extracting embedded
  files"), so the app randomly wouldn't start. .NET Framework 4.8 ships with Windows 10 (1903+)
  and 11, so the exe is a ~250 KB single file with nothing to unpack. Do not switch back to
  self-contained single-file publishing, and do not make it framework-dependent .NET (Core).
- **Build:** `dotnet build MonitorSwitcher/MonitorSwitcher.csproj -c Release` →
  `bin/Release/net48/MonitorSwitcher.exe` (the generated `.exe.config` is optional).
- **No NuGet packages.** Adding one would add DLLs next to the exe. Everything uses the
  .NET Framework class library plus Win32 P/Invoke (e.g. JSON is `DataContractJsonSerializer`,
  not `System.Text.Json`).
- **Language/BCL limits on net48:** avoid APIs missing from .NET Framework (`Math.Clamp`,
  `Dictionary.TryAdd`, `string.Contains(char)`, `KeyValuePair` deconstruction, ranges/indices,
  `init`/records without polyfills).

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
│   ├── ConfigService.cs        # Load/save config.json from Documents
│   ├── Log.cs                  # monitor-switcher.log next to config.json
│   └── NativeMethods.cs        # All P/Invoke declarations
├── ViewModels/
│   ├── MainViewModel.cs        # Top-level VM, owns monitors and presets
│   ├── MonitorViewModel.cs     # Wraps MonitorInfo for binding, switching and reading
│   ├── InputEntryViewModel.cs  # Wraps InputEntry, exposes IsActive
│   ├── PresetViewModel.cs      # One-click "switch several monitors" button
│   └── RelayCommand.cs         # Minimal ICommand implementation
├── Views/
│   ├── ConfigWindow.xaml        # Monitor configuration dialog
│   ├── ConfigWindow.xaml.cs
│   ├── IdentifyWindow.xaml      # Full-screen overlay to identify a monitor
│   └── IdentifyWindow.xaml.cs
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
    // Stable identity: the monitor device path from QueryDisplayConfig /
    // DISPLAYCONFIG_TARGET_DEVICE_NAME (e.g. \\?\DISPLAY#DELA1F3#...). Survives Windows
    // renumbering \\.\DISPLAYn. Falls back to "\\.\DISPLAYn#index" if unavailable.
    public string Id { get; set; }

    // Display name shown in UI
    public string Name { get; set; }           // e.g. "Monitor 1 – DELL U2722D"

    // GDI name from MONITORINFOEX.szDevice. Can change when monitors reconnect.
    public string DeviceName { get; set; }

    // Which physical monitor behind the HMONITOR (> 0 only when duplicating displays)
    public int PhysicalIndex { get; set; }

    // False when Windows no longer lists it (e.g. a DisplayPort monitor on another source)
    public bool IsConnected { get; set; }

    // Current detected input (0 = unknown / DDC read failed)
    public uint CurrentInput { get; set; }

    // User-configured list of available inputs for this monitor
    public List<InputEntry> Inputs { get; set; } = new();

    // When true, this monitor is hidden from the main window
    public bool IsDisabled { get; set; }
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

1. `QueryDisplayConfig` + `DisplayConfigGetDeviceInfo` map each `\\.\DISPLAYn` to its
   monitors' device paths (the stable `Id`) and EDID names (e.g. "DELL U2722D").
2. `EnumDisplayMonitors` + `GetMonitorInfo` give each display's `HMONITOR` and bounds.
3. One `MonitorInfo` per physical monitor, sorted left to right as arranged in Windows.
4. Do **not** keep `HMONITOR` or physical handles. They go stale whenever Windows
   re-enumerates displays (e.g. when another monitor switches away), which made every
   later switch fail.

### Every DDC/CI operation (`ReadCurrentInput`, `SetInput`, `DetectSupportedInputs`)

1. Takes the per-monitor lock (`SemaphoreSlim` keyed by `Id`, with a timeout → `Busy`).
   Overlapping DDC traffic on the same monitor (e.g. a read triggered by window activation
   during a switch) corrupts both, so all access is serialized per monitor.
2. Re-resolves the current `HMONITOR` by `Id` (never by a cached handle; by `DeviceName`
   only when device paths aren't available at all).
3. `GetPhysicalMonitorsFromHMONITOR`, uses `PhysicalIndex`, `DestroyPhysicalMonitors` in `finally`.
4. Leaves ≥ 50 ms between commands to the same monitor (DDC/CI timing).
5. Returns a `DdcStatus` (`Ok`, `NotConnected`, `NoDdc`, `Failed`, `Busy`). Never throws.

### `ReadCurrentInput` → `uint`

Returns `pdwCurrentValue & 0xFF` (input source is a one-byte value; some monitors put
junk in the high byte), or `0` on failure.

### `SetInput` → `DdcStatus`

- Retries (up to 3 attempts) only when `SetVCPFeature` fails or the monitor briefly isn't
  listed. Never re-send a command that succeeded.
- Do **not** read back right after switching to "verify" and re-send. Monitors are busy
  for a while after a switch, so early reads fail or return the old input, and the
  re-sent command interfered with the switch. `MonitorViewModel` instead reads once
  ~3 s later to show the real state.
- If the monitor disappears after an unacknowledged attempt, treat it as switched:
  that's what a DisplayPort monitor does once it has moved to another source.

### Error handling contract

- DDC calls can fail silently (monitor off, cable type, firmware bugs).
- Wrap every Win32 call in try/catch. Never propagate exceptions to the UI.
- If a monitor doesn't support DDC, mark it in `MonitorInfo.CurrentInput = 0`
  and disable its input buttons in the UI.

### Logging

- Use `Log.Info/Warn/Error` (Core/Log.cs). It appends to `monitor-switcher.log` next to
  `config.json` (rolled at 512 KB) and also writes to `Trace`.
- Log failures and switches, not every routine read.

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
- **Refresh button**: re-enumerates monitors and re-reads inputs; the icon spins while
  busy. A new refresh cancels the previous one via `CancellationTokenSource`.
  Refresh also runs automatically (debounced 1.5 s) on `WM_DISPLAYCHANGE` and resume.
  Existing `MonitorViewModel`s are updated in place, matched by `Id`.
- **Disconnected monitors**: a monitor that disappears while the app runs keeps its card,
  marked "Not connected", with buttons disabled, so it's clear why it can't be switched.
- **Presets card** (above the monitors, only when presets exist): one button per preset;
  it sends all of the preset's switch commands at once (in parallel). The preset whose
  inputs are all current is highlighted.
- **Window activation** re-reads inputs quietly: throttled to every 2 s, delayed 400 ms
  so a click that activated the window gets its switch in first, skipped for monitors
  switched in the last 3 s, and discarded if a switch started meanwhile.
- **Buttons** are disabled only while that monitor is switching (not while reading).
  Call `RelayCommand.RaiseCanExecuteChanged()` when that changes. Relying on
  `CommandManager` left buttons disabled until the next mouse event, eating clicks.
- **Configure button**: opens a `ConfigWindow` (separate window, modal).

### Threading rule

All `MonitorService` calls are blocking Win32 calls. **Always run them on a
`Task.Run` background thread** and `await` them from the UI thread, so continuations
update `ObservableCollection` and view-model properties on the UI thread.

---

## Configure Window — `ConfigWindow.xaml`

Allows the user to set up the input list for each monitor.

```
┌─────────────────────────────────────────────┐
│  Configure Monitors                     [X]  │
├─────────────────────────────────────────────┤
│  \\.\DISPLAY1              [🖥 Identify]     │
│  Name: [DELL U2722D          ]  ☐ Hidden     │
│  Inputs:                    [🔍 Detect]      │
│   [HDMI 1  ] (VCP 17) [▲][▼] [  ✕  ]        │
│   [HDMI 2  ] (VCP 18) [▲][▼] [  ✕  ]        │
│   [DisplayP] (VCP 15) [▲][▼] [  ✕  ]        │
│   Add: [preset dropdown] [+ Add]             │
│   Manual: [label] [vcp] [+ Add]              │
├─────────────────────────────────────────────┤
│  Monitor 2                                   │
│  ... (same pattern)                          │
├─────────────────────────────────────────────┤
│  Config: %DOCS%\MonitorSwitcher\config.json  │
│                         [Cancel]  [Save]     │
└─────────────────────────────────────────────┘
```

- Edits are made on a **copy** of the config; only committed on Save.
- VCP Value field: numeric `TextBox` with input validation (numbers only, 0–255).
- **Identify button**: shows a full-screen overlay on the monitor for 3 seconds
  (like Windows display settings Identify). Implemented via `IdentifyWindow`.
- **Hidden checkbox**: marks the monitor as disabled — it won't appear in the main
  window. Useful for laptop screens that don't support DDC/CI.
- **Config path**: shown at the bottom with an "Open Folder" button.
- **Presets section** (below the monitors): name + one dropdown per monitor
  ("Don't change" or one of that monitor's inputs; stays in sync as inputs are edited).
- On Save: reload `config.json`, replace the entries for the monitors shown, **keep entries
  for monitors not shown** (e.g. unplugged right now), and replace the presets (keeping
  targets for monitors not shown). Show an error and stay open if saving fails.

---

## Persistence — `config.json`

Store configuration in a JSON file in the user's Documents folder, via
`DataContractJsonSerializer` (`[DataContract]`/`[DataMember(Name = "...")]` classes).

- **Path:** `Path.Combine(Environment.GetFolderPath(SpecialFolder.MyDocuments), "MonitorSwitcher", "config.json")`
- This avoids creating a visible config file next to the exe.
- On first load, migrates any existing `appsettings.json` from the exe directory
  to the new location automatically.
- Exposes `ConfigService.ConfigFilePath` (static property) for display in the UI.

```json
{
  "alwaysOnTop": false,
  "monitors": [
    {
      "monitorId": "\\\\?\\DISPLAY#DELA1F3#5&1a2b3c4d&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
      "deviceName": "\\\\.\\DISPLAY2",
      "displayName": "DELL U2722D",
      "inputs": [
        { "label": "DisplayPort-1", "vcpValue": 15 },
        { "label": "HDMI-2", "vcpValue": 18 }
      ],
      "isDisabled": false
    }
  ],
  "presets": [
    {
      "name": "Laptop",
      "targets": [
        { "monitorId": "\\\\?\\DISPLAY#DELA1F3#...", "deviceName": "\\\\.\\DISPLAY2", "vcpValue": 18 }
      ]
    }
  ]
}
```

- Match saved monitors to detected monitors by `monitorId`. Entries from older versions
  have only `deviceName`: match those by `deviceName` once, then write the `monitorId`.
  If a `monitorId` no longer matches but exactly one detected monitor of the same model
  (the `DELA1F3` part) is unmatched, adopt it (cable moved to another port).
- If a monitor is detected but has no saved config, show it with an empty input list
  and prompt the user to configure it.
- `isDisabled: true` hides the monitor from the main window.
- Preset targets reference monitors the same way (`monitorId`, else `deviceName`).

---

## MVVM Pattern

Use a minimal MVVM approach — no MVVM framework needed.

- `MainViewModel` implements `INotifyPropertyChanged`.
- Commands implemented as `RelayCommand` (write a simple one with `Action` + `Func<bool>`).
- `ObservableCollection<MonitorViewModel>` — one per detected monitor.
- `MonitorViewModel` wraps `MonitorInfo` and exposes bindable properties:
  `DisplayName`, `CurrentInputLabel`, `Inputs`, `IsSwitching`, `IsConnected`,
  `StatusMessage` (last error or "not connected"), `SetInputCommand`, plus
  `SwitchToAsync(vcp)` used by presets.
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

Owns all `config.json` I/O. Keeps `MonitorService` focused on DDC/CI only.

- `Load()` → `AppConfig` — reads and deserializes the JSON file, then fills in anything
  missing (the serializer doesn't run constructors or initializers). Migrates legacy
  `appsettings.json` from the exe directory. An unreadable file is copied to
  `config.invalid.json` before starting empty.
- `TrySave(AppConfig, out error)` — writes a temp file, then replaces `config.json`
  (with retries for brief locks). Creates the `Documents/MonitorSwitcher/` directory.
- `ConfigFilePath`, `ConfigDirectory` (static properties) — for display and the log.
- `ConfigExists()` — checks both new and legacy locations.

---

## Startup Behavior

0. **Single instance:** a named mutex. A second launch signals the running copy to show
   its window (moved to the monitor under the cursor) and exits. Two copies talking DDC/CI
   to the same monitors make switching unreliable. Unhandled exceptions are logged and
   shown instead of silently killing the app.
1. On load, call `MonitorService.GetMonitors()` on a background thread.
2. Load `config.json` via `ConfigService` and merge saved config into monitor list.
3. `MainViewModel` stores all monitors in `_allMonitorViewModels`, but only adds
   non-disabled monitors to the displayed `Monitors` collection.
4. For each visible monitor, attempt to read current input via DDC on a background thread.
5. Show the window — cards appear immediately, input states update as DDC reads complete.
6. **First run (no `config.json`):** detect all monitors and open the
   `ConfigWindow` automatically so the user can set up inputs.

---

## Important Caveats to Handle Gracefully

| Situation | Behavior |
|-----------|----------|
| Switch fails | Restore the previous input in the UI, show the reason on the card |
| DDC read times out / returns 0 | Show "Current input: unknown", buttons still work |
| Physical handle open fails | Log it, show error state in card |
| Monitor list changes (plugged/unplugged, switched away) | Auto-refresh on `WM_DISPLAYCHANGE`; Refresh button re-enumerates |
| Monitor vanishes while app is open | Keep its card, marked "Not connected", buttons disabled |
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
6. **Keep the exe a single file.** Config is stored in `Documents/MonitorSwitcher/config.json`,
   not next to the exe. Legacy `appsettings.json` is migrated on first load. Don't add
   NuGet packages or self-contained .NET publishing (see Project Setup for why).
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
