# CLAUDE.md

Guidance for working in this repository.

## What this is

A C# **WPF** desktop app that displays and processes up to **4 cameras** on one Matrox
**Rapixo CXP** (CoaXPress) frame-grabber board via the **MIL** (Matrox Imaging Library) API.
Live grab + per-frame processing, with per-camera exposure / acquisition-rate / trigger /
white-balance / DCF control, fit-to-window + mouse zoom/pan, double-click fullscreen, snapshot,
and **two recording modes** (see below).

For a line-by-line walkthrough of `src/` — per-file roles, every handled exception case, and the
reasoning behind each MIL workaround — see [research.md](research.md).

## Prerequisites

- **MIL 10.70** installed (`C:\Program Files\Matrox Imaging\MIL`). The MIL .NET NuGet packages
  are consumed from the local source registered in `src/nuget.config`
  (`C:\Program Files\Matrox Imaging\MIL\MIL.NET\NuGet`).
- A **Rapixo CXP** board with cameras for live grab (without hardware the app falls back to the
  default MIL system and shows "No camera" panes).
- **x64** only (MIL NuGet supports x64/arm64 only). Target framework **net6.0-windows**
  (installed WindowsDesktop runtime; matches the shipped MIL WPF examples).
- **ffmpeg.exe** for recording — not a NuGet dependency; resolved at runtime (configured path →
  `PATH` → WinGet → `C:\ffmpeg\bin`). If it isn't found, `CanRecord` is false and **both** record
  buttons are disabled. All encoding goes through ffmpeg; no MIL compression licence is used.

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
  MatroxFrameGrabber.csproj    SDK-style, UseWPF + UseWindowsForms (folder picker only), x64
  nuget.config                 MIL.NET local package source
  App.xaml(.cs)                MatroxFrameGrabber
  Views/                       MatroxFrameGrabber.Views
                                 MainWindow, CameraPaneView, Styles.xaml (dark theme)
  ViewModels/                  MatroxFrameGrabber.ViewModels (MainViewModel)
  Mil/                         MatroxFrameGrabber.Mil
                                 MilApplicationManager, CameraChannel,
                                 GenICamFeatures, RecordingSession
  Infrastructure/              MatroxFrameGrabber.Infrastructure
                                 OutputSettings, FfmpegRecorder, RawFrameWriter,
                                 RawSegmentSession, RelayCommand, NativeMethods
docs/
research.md                    in-depth src/ analysis
```

`CameraChannel` is the core: per camera it does `MdigAlloc(M_DEV0+i)` + `MdispAlloc(M_WPF)` +
display buffer + grab ring + `MdigProcess` hook, plus GenICam feature control. It is **both model
and view-model** (implements `INotifyPropertyChanged`, exposes `RelayCommand`s, and is bound
directly as a pane's `DataContext`) — that's why it's ~1400 lines.
`MilApplicationManager` owns the shared app/system and creates one `CameraChannel` per channel.

## The two recording modes

They are mutually exclusive per camera and have **deliberately opposite back-pressure policies**.

| | `● Rec` (live colour) | `◆ RAW` (lossless) |
|---|---|---|
| Class | `RecordingSession` | `RawSegmentSession` + `RawFrameWriter` |
| Source | display buffer (3-band colour) | grab buffer (1-band Bayer) |
| Path | MIL → memory → ffmpeg **stdin pipe** | MIL → local `.raw` segments → ffmpeg **batch** |
| Pixel format | `gbrp` / `gray` | `bayer_rggb8` |
| Under load | **drops frames** (live view wins) | **blocks the hook** (no frame is lost) |
| Preview | normal colour | **grayscale**, every 6th frame |
| Board state | untouched | `M_BAYER_CONVERSION` **disabled** |
| Resolution preset | honoured | always full native |

RAW writes segments to a local scratch folder (fast NVMe) and only the converted MP4s go to the
output folder, which may be on a NAS — RAW is far too fast for network storage.

## Gotchas (learned the hard way)

- The board reports **4 digitizers even when fewer cameras are connected**; `MdigAlloc` on an
  empty port raises a modal MIL error dialog even under `M_THROW_EXCEPTION`. The probe is wrapped
  in `MappControl(M_ERROR, M_PRINT_DISABLE/ENABLE)` — keep that, and always restore in a `finally`
  (otherwise every later MIL error disappears silently).
- **`M_BAYER_CONVERSION` is a persistent board setting.** Turning it off for RAW capture survives
  the grab, the app, and a restart — after which the colour pipeline misreads raw/mono data as a
  tiled, garbled image. It is re-asserted on every `AllocateCamera` (before inquiring
  `M_SIZE_BAND`), and `RestoreColorAfterRaw()` turns it back on **first**, before anything else
  that could fault. Any new code that disables it must guarantee the same restore.
- A `MILWPFDisplay` constructed with an unbound/zero `DisplayId` pops a native MIL error dialog,
  so the display controls are created **lazily in code** once a channel with a valid DisplayId
  exists (see `CameraPaneView` and the fullscreen overlay in `MainWindow`). `MainWindow`'s
  constructor allocates MIL and sets `DataContext` *before* `InitializeComponent()` for the same
  reason.
- Grab buffers use scarce **non-paged/DMA memory**; the display buffer drops `M_GRAB` (paged) and
  the grab ring is kept small (4; 24 for RAW, whose band-1 frames are ~3x smaller). Each
  allocation is individually guarded — a shortfall degrades instead of aborting startup. Increase
  MIL's non-paged pool in **MILConfig** if needed.
- **`MbufGet` copies the row-padded buffer** (pitch 2112 > width 2064), which shears the image
  when read back as tight rows. Use `MbufGet2d` when you need the logical W×H region packed.
- **Do not use `MbufGetColor` to extract colour bytes.** Its packing path hangs on these buffers
  and the planar path silently returns zeros. Allocate a planar 3-band buffer, make per-band
  children with `MbufChildColor`, `MbufGet` each band, and feed ffmpeg planar `gbrp` — and free
  the children **before** the parent.
- A `MILWPFDisplay` with `M_KEYBOARD_USE` makes MIL subclass the top-level HWND and swallow key
  messages before WPF turns them into routed events, so `PreviewKeyDown` never fires. Fullscreen
  ESC is caught via `ComponentDispatcher.ThreadFilterMessage` instead.
- Interactive zoom/pan is native MIL (`M_MOUSE_USE`/`M_KEYBOARD_USE`); fit-to-window uses
  `M_SCALE_DISPLAY, M_ONCE` — `M_ENABLE` would lock manual zoom/pan.
- Anything you add to the `MdigProcess` hook runs in the acquisition budget. Heavy work doesn't
  just lag the preview, it stalls the grab (and on the RAW path shows up immediately as
  `M_PROCESS_FRAME_MISSED`).
- UI state is refreshed by **one 500 ms `DispatcherTimer`** calling `RefreshStats()` on every
  channel. To surface a new value, raise it there; only three things are pushed as events
  (`RecordingFailed`, `CameraLost`, `RawRecordingFinished`).
