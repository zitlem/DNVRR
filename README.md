# DNVRR

Desktop NVR viewer for Hikvision systems. Native Windows app built with WPF and .NET 9.

## Features

- **Live camera preview** via Hikvision HCNetSDK with GPU-accelerated decoding
- **Multi-window support** — open multiple viewer windows across monitors
- **Custom views** — save grid layouts with specific camera assignments
- **Drag-and-drop** — drag cameras from the sidebar to grid slots, rearrange tiles
- **PTZ control** — pan, tilt, zoom, presets for supported cameras
- **Network discovery** — two-phase scan (ONVIF WS-Discovery + ISAPI subnet probe)
- **Auto-detect ports** — HTTP and SDK ports discovered automatically
- **Connection status** — real-time status lights (green/yellow/red) with periodic probing
- **Collapsible sidebars** — left (views/cameras) and right (PTZ/controls) panels
- **Session persistence** — remembers window positions, sizes, active views, and sidebar state
- **Single-file deployment** — SDK DLLs embedded and auto-extracted on first run
- **Dark theme** — matches the NVRR web interface color scheme

## Download

Get the latest release from the [Releases](../../releases) page:

- **DNVRR.exe** — portable single-file executable, no installation needed
- **DNVRR-Setup-x.x.x.exe** — installer with Start Menu and desktop shortcuts

## Quick Start

1. Run `DNVRR.exe`
2. Click **Admin Panel** in the right sidebar
3. Click **Scan** to discover NVRs on your network
4. Click **Use** on a discovered device, enter credentials, click **Connect**
5. Cameras appear in the left sidebar — drag them to the grid

## Admin Panel

- **Scan Network** — select a network adapter, scan finds devices via ONVIF and ISAPI
- **Add NVR** — enter IP, credentials; HTTP port and SDK port are auto-detected
- **Rename NVR** — click the pencil icon next to the NVR name
- **Camera toggles** — enable/disable cameras and PTZ from the cameras list

## Viewer

- **Views** — create named grid layouts, switch between them in the left sidebar
- **Grid size** — use preset buttons (2x2, 3x3, 4x3, 4x4) or custom cols/rows
- **Assign cameras** — click or drag from the sidebar to a grid slot
- **Rearrange** — drag tiles within the grid to swap positions
- **Remove** — right-click a tile to remove it from the view
- **Fullscreen** — double-click any camera feed (double-click or Escape to exit)
- **Multiple windows** — click "New Window", each window has its own view
- **Close Window** — closes just that window; X button closes all windows

## Building

Requires [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and Windows.

```bash
dotnet build DNVRR
dotnet run --project DNVRR
```

### Publish portable exe

```bash
dotnet publish DNVRR -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### Hikvision SDK

The SDK DLLs are embedded as resources and auto-extracted to `%LOCALAPPDATA%\DNVRR\sdk\` on first run. To update the SDK, replace the files in `DNVRR/sdk/` and rebuild.

## Data

All data is stored next to the executable in a `data/` folder:

- `dnvrr.db` — SQLite database (NVRs, cameras, views, window state)
- `dnvrr.log` — log file for troubleshooting

## License

Private repository.
