# Matrox Rapixo CXP — Multi-Camera Viewer

A C# **WPF** application that displays and processes up to **4 cameras** on a single Matrox
**Rapixo CXP** (CoaXPress) frame-grabber board using the **MIL** (Matrox Imaging Library) API.

## Features

- 2×2 live view of up to 4 cameras (one Rapixo CXP board, `MdigProcess` grab + per-frame hook)
- Per-camera control via the GenICam feature layer: **exposure** (+ auto), **trigger**
  (mode / source / software trigger), **white balance** (auto / once / R·B ratios), **DCF** load,
  and the MIL **GenICam feature browser**
- **Fit-to-window** (no cropping) + mouse-wheel **zoom** / drag **pan**, **1:1**, and
  **double-click fullscreen** (ESC to exit)
- **Snapshot** save (PNG/BMP/TIFF/JPEG), per-camera and global start/stop

## Requirements

- Windows x64, **MIL 10.70** installed, .NET SDK with the WindowsDesktop (net6.0) runtime
- A Rapixo CXP board with cameras (falls back to "No camera" panes without hardware)

## Build & run

```bash
dotnet build MatroxFrameGrabber.slnx -c Release
```

Run `src/bin/x64/Release/net6.0-windows/MatroxFrameGrabber.exe`.

## Project layout

```
MatroxFrameGrabber.slnx     solution
src/                        source (Views / ViewModels / Mil / Infrastructure)
docs/                       documentation
```

See [CLAUDE.md](CLAUDE.md) for build details and implementation notes.
