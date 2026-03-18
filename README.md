# MonitorSwitcher

A lightweight, portable .NET WPF desktop application that lets you switch input sources on DDC/CI-capable monitors. No installer, no admin rights required.

## Features

- **Switch monitor inputs** (HDMI, DisplayPort, USB-C, etc.) with one click
- **Auto-detect** supported inputs via DDC/CI capabilities
- **Configure** custom input labels and VCP values per monitor
- **Identify monitors** with an on-screen overlay (like Windows display settings)
- **Hide monitors** you don't need (e.g., laptop screens)
- **Stay-on-top** mode for quick access
- **Portable** single-file executable — no installation needed

## Requirements

- Windows 10/11 (64-bit)
- Monitors with DDC/CI support (most modern monitors)
- No admin rights required

## Usage

1. Download the latest `MonitorSwitcher.exe` from [Releases](../../releases)
2. Run it from any folder
3. On first launch, configure your monitors' inputs
4. Click input buttons to switch sources

## Configuration

Configuration is stored in:

```
%USERPROFILE%\Documents\MonitorSwitcher\config.json
```

The config path is shown at the bottom of the Configure window with an **Open Folder** button for quick access.

## Building

```bash
dotnet publish MonitorSwitcher/MonitorSwitcher.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

The output is a single `.exe` that runs from any folder.

## How It Works

MonitorSwitcher uses the DDC/CI protocol (Display Data Channel / Command Interface) via the Windows `dxva2.dll` API to communicate with monitors over their display cables. VCP code `0x60` controls the input source selection.

> **Note:** VCP values for input sources are not standardized across manufacturers. The app lets you configure which values correspond to which inputs for each monitor. Use the **Detect Inputs** button to auto-discover supported values, or add them manually.

## Troubleshooting

| Issue | Solution |
|-------|---------|
| Monitor shows "DDC not supported" | Your monitor may not support DDC/CI, or it may be disabled in the monitor's OSD settings |
| Current input shows "unknown" | The monitor may be asleep or the DDC read timed out — input buttons still work |
| Wrong input switches | VCP values differ by manufacturer — try different values in Configure |
| Laptop screen appears | Use the **Hidden** checkbox in Configure to hide it from the main window |
