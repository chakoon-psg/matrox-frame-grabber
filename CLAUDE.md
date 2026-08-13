# CLAUDE.md

Guidance for working in this repository.

## What this is

A C# **WPF** desktop app that displays and processes up to **4 cameras** on one Matrox
**Rapixo CXP** (CoaXPress) frame-grabber board via the **MIL** (Matrox Imaging Library) API.
Live grab + per-frame processing, with per-camera exposure / trigger / white-balance / DCF
control, fit-to-window + mouse zoom/pan, double-click fullscreen, and snapshot.

## Prerequisites

- **MIL 10.70** installed (`C:\Program Files\Matrox Imaging\MIL`). The MIL .NET NuGet packages
  are consumed from the local source registered in `src/nuget.config`
  (`C:\Program Files\Matrox Imaging\MIL\MIL.NET\NuGet`).
- A **Rapixo CXP** board with cameras for live grab (without hardware the app falls back to the
  default MIL system and shows "No camera" panes).
- **x64** only (MIL NuGet supports x64/arm64 only). Target framework **net6.0-windows**
  (installed WindowsDesktop runtime; matches the shipped MIL WPF examples).

## Build & run

```bash
dotnet build MatroxFrameGrabber.slnx -c Release
```

Output exe (note the `x64` segment — `Platforms=x64` nests output under `bin\x64\`):

```
src\bin\x64\Release\net6.0-windows\MatroxFrameGrabber.exe
```

## Layout

```
MatroxFrameGrabber.slnx        solution (root)
src/
  MatroxFrameGrabber.csproj    SDK-style, UseWPF, x64
  nuget.config                 MIL.NET local package source
  App.xaml(.cs)                MatroxFrameGrabber
  Views/                       MatroxFrameGrabber.Views      (MainWindow, CameraPaneView)
  ViewModels/                  MatroxFrameGrabber.ViewModels (MainViewModel)
  Mil/                         MatroxFrameGrabber.Mil        (MilApplicationManager, CameraChannel)
  Infrastructure/              MatroxFrameGrabber.Infrastructure (RelayCommand)
docs/
```

`CameraChannel` is the core: per camera it does `MdigAlloc(M_DEV0+i)` + `MdispAlloc(M_WPF)` +
display buffer + grab ring + `MdigProcess` hook, plus GenICam feature control.
`MilApplicationManager` owns the shared app/system and creates one `CameraChannel` per channel.

## Gotchas (learned the hard way)

- The board reports **4 digitizers even when fewer cameras are connected**; `MdigAlloc` on an
  empty port raises a modal MIL error dialog even under `M_THROW_EXCEPTION`. The probe is wrapped
  in `MappControl(M_ERROR, M_PRINT_DISABLE/ENABLE)` — keep that.
- A `MILWPFDisplay` constructed with an unbound/zero `DisplayId` pops a native MIL error dialog,
  so the display controls are created **lazily in code** once a channel with a valid DisplayId
  exists (see `CameraPaneView` and the fullscreen overlay in `MainWindow`).
- Grab buffers use scarce **non-paged/DMA memory**; the display buffer drops `M_GRAB` (paged) and
  the grab ring is kept small (4). Increase MIL's non-paged pool in **MILConfig** if needed.
- Interactive zoom/pan is native MIL (`M_MOUSE_USE`/`M_KEYBOARD_USE`); fit-to-window uses
  `M_SCALE_DISPLAY, M_ONCE`.
