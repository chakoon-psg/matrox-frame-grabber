using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Threading;
using MatroxFrameGrabber.Infrastructure;
using MatroxFrameGrabber.Mil;
using MatroxFrameGrabber.Mil.Video;

namespace MatroxFrameGrabber.ViewModels
{
    /// <summary>A per-camera settings group that can be copied to every other camera at once.</summary>
    public enum CameraSettingKind { Exposure, AcqRate, Trigger, WhiteBalance }

    /// <summary>
    /// Binds the camera channels, output settings, and the Start/Stop/Record commands to the
    /// main window, and drives a UI-thread timer that refreshes per-channel live statistics.
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly MilApplicationManager _manager;
        private readonly DispatcherTimer _statsTimer;

        // One CSV per grab, opened on the tick that first sees a channel grabbing and closed on the
        // tick that sees the last one stop. Driven from the tick rather than from StartAll/StopAll
        // because panes start and stop channels individually too, and a run that began from a pane
        // is worth just as much as one that began from the toolbar.
        private readonly BrightnessLog _brightnessLog = new BrightnessLog();

        public MainViewModel(MilApplicationManager manager)
        {
            _manager = manager;

            // Always enabled: cameras can also be started/stopped individually from their panes,
            // so gating these on a single "running" flag would desync.
            StartAllCommand = new RelayCommand(StartAll);
            StopAllCommand = new RelayCommand(StopAll);

            // The brightness strip is always on screen, so measurement is enabled for the whole
            // session — set once here rather than pushed every tick, since nothing turns it off.
            // A channel that is not grabbing still costs nothing: CameraChannel.RefreshStats
            // only samples while it has a live display buffer.
            foreach (var channel in _manager.Channels)
                channel.BrightnessEnabled = true;

            // Detection likewise for the whole session, unless the command line switched it off.
            // The switch exists for one measurement: frames missed with the tile reduction on the
            // acquisition path against the same run without it.
            foreach (var channel in _manager.Channels)
                channel.DetectionEnabled = !App.DetectionOff;

            // Run the stats timer for the whole session so per-pane Start also updates fps/status.
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statsTimer.Tick += (s, e) =>
            {
                foreach (var channel in _manager.Channels)
                    channel.RefreshStats();
                RecordMeasurementRow();
                RaiseChanged(nameof(AnyRecording));
                RaiseChanged(nameof(BandwidthText));
                StatsRefreshed?.Invoke();
            };
            _statsTimer.Start();
        }

        /// <summary>
        /// Appends this tick's readings to the measurement CSV, opening or closing the file as the
        /// set of grabbing channels changes.
        ///
        /// Why the readings are logged at all: the detector's depth threshold has to clear the
        /// false-positive floor, and that floor is a property of the rig — how much the luma of a
        /// healthy panel wanders over minutes under this lighting, this lens, and this exposure.
        /// It can only be measured, and a run of any length is the measurement. Missed frames and
        /// fps ride along in the same rows so the acceptance check for a payload or hook change is
        /// answered by the same file, from the same seconds, as the brightness it may have cost.
        /// </summary>
        private void RecordMeasurementRow()
        {
            bool anyGrabbing = false;
            foreach (var channel in _manager.Channels)
            {
                if (channel.IsGrabbing) { anyGrabbing = true; break; }
            }

            if (!anyGrabbing)
            {
                if (_brightnessLog.IsActive)
                {
                    _brightnessLog.Stop();
                    RaiseChanged(nameof(MeasurementLogText));
                }
                return;
            }

            if (!_brightnessLog.IsActive)
            {
                if (!_brightnessLog.Start(BrightnessLog.DefaultFolder, DateTime.Now))
                    return;   // Start already logged the reason; a run without a log still runs.
                RaiseChanged(nameof(MeasurementLogText));
            }

            DateTime now = DateTime.Now;
            foreach (var channel in _manager.Channels)
            {
                if (!channel.IsGrabbing) continue;

                var history = channel.Brightness;
                var latest = history.Latest;
                _brightnessLog.Append(
                    now, channel.Name, channel.Decimation, channel.ExposureUs,
                    channel.AnalysisRoi.Width, channel.AnalysisRoi.Height,
                    channel.FrameCount, channel.FrameRate, channel.FramesMissed,
                    history.HasData, latest.Luma, latest.ClippedPct, latest.BlackPct,
                    // LastReduceUs, not the running maximum: a monotonic column cannot show a
                    // distribution, and what the cost does over a run is the question.
                    channel.AnomalyCount, channel.LastReduceUs,
                    channel.GridsAccepted, channel.DetectionDepth,
                    channel.DetectionCoherence, channel.DetectionBaseline);
            }

            RaiseChanged(nameof(MeasurementLogText));
        }

        /// <summary>Where this run's measurements are going, for the status bar.</summary>
        public string MeasurementLogText =>
            _brightnessLog.IsActive
                ? $"log {System.IO.Path.GetFileName(_brightnessLog.Path)} ({_brightnessLog.Rows} rows)"
                : string.Empty;

        /// <summary>Closes the measurement file. Called from the window's Closing handler.</summary>
        public void StopMeasurementLog() => _brightnessLog.Stop();

        /// <summary>The camera channels, bound by index in the XAML.</summary>
        public IReadOnlyList<CameraChannel> Channels => _manager.Channels;

        /// <summary>App-wide output folder + resolution settings.</summary>
        public OutputSettings Output => _manager.Output;


        /// <summary>Board / system summary shown in the header.</summary>
        public string SystemStatus =>
            $"System: {_manager.AllocatedSystemDescriptor}   Digitizers: {_manager.DigitizerCount}";

        /// <summary>
        /// Aggregate host DMA load across the running channels, against the measured ceiling.
        ///
        /// Computed from the live frame rate rather than an assumed one, so it reports what is
        /// actually crossing the bus. Warns above ChannelRoi.WarnBytesPerSecond (80% of the
        /// ceiling) — past that the board starts dropping frames and picks the victim channel
        /// itself, unfairly.
        /// </summary>
        public string BandwidthText
        {
            get
            {
                double total = 0;
                foreach (var channel in _manager.Channels)
                {
                    if (!channel.IsGrabbing) continue;
                    total += channel.FrameRate * channel.BytesPerFrame;
                }
                if (total <= 0)
                    return "";
                string s = $"DMA {total / 1e9:F2} GB/s";
                return total >= ChannelRoi.WarnBytesPerSecond
                    ? s + $" ⚠ over budget ({ChannelRoi.HostDmaCeilingBytesPerSecond / 1e9:F1} GB/s ceiling)"
                    : s;
            }
        }

        /// <summary>True if any camera is currently recording (drives the Rec-All toggle).</summary>
        public bool AnyRecording
        {
            get
            {
                foreach (var channel in _manager.Channels)
                    if (channel.IsRecording)
                        return true;
                return false;
            }
        }

        /// <summary>
        /// Which backend is in force, as two radio buttons rather than three.
        ///
        /// Auto stays the stored default and is not offered: a third radio labelled "Auto" would
        /// leave the operator unable to tell what is actually recording. Instead the pair shows the
        /// effective choice, so on a fresh install it reads FFMPEG because that is what runs, and
        /// picking one stores it explicitly - which also turns off the fallback, so a MIL sink
        /// under test cannot quietly hand over to ffmpeg.
        /// </summary>
        public bool SinkIsFfmpeg
        {
            get => EffectiveSink() == SinkChoice.Ffmpeg;
            set { if (value) SetSink(VideoSinkPreference.Ffmpeg); }
        }

        public bool SinkIsMil
        {
            get => EffectiveSink() == SinkChoice.Mil;
            set { if (value) SetSink(VideoSinkPreference.Mil); }
        }

        /// <summary>Whether the MIL radio can be picked at all - see VideoSinkPolicy.MilSelectable.</summary>
        public bool MilSinkSelectable => VideoSinkFactory.MilSelectable(out _);

        /// <summary>
        /// What to say about the MIL option, selectable or not. A radio that cannot be picked has
        /// to explain itself, and the explanation is MIL's own words when it has any.
        /// </summary>
        public string MilSinkReason
        {
            get
            {
                VideoSinkFactory.MilSelectable(out string reason);
                return reason;
            }
        }

        /// <summary>The backend actually chosen, and why. Shown under the radios.</summary>
        public string SinkReasonText
        {
            get
            {
                VideoSinkPolicy.Choose(Output.VideoSink, VideoSinkFactory.Readiness(out _),
                                       !string.IsNullOrEmpty(FfmpegRecorder.ResolveFfmpegPath(Output.FfmpegPath)),
                                       out string reason);
                return reason;
            }
        }

        private SinkChoice EffectiveSink() =>
            VideoSinkPolicy.Choose(Output.VideoSink, VideoSinkFactory.Readiness(out _),
                                   !string.IsNullOrEmpty(FfmpegRecorder.ResolveFfmpegPath(Output.FfmpegPath)),
                                   out _);

        private void SetSink(VideoSinkPreference preference)
        {
            if (Output.VideoSink == preference) return;
            Output.VideoSink = preference;   // persists
            RaiseChanged(nameof(SinkIsFfmpeg));
            RaiseChanged(nameof(SinkIsMil));
            RaiseChanged(nameof(SinkReasonText));
            MilErrorLog.Note($"settings: recording backend set to {preference}"
                           + " (takes effect on the next start - the sink is chosen when a camera is allocated)");
        }

        /// <summary>True if at least one camera supports recording (i.e. something can encode; see docs/adr/).</summary>
        public bool AnyCanRecord
        {
            get
            {
                foreach (var channel in _manager.Channels)
                    if (channel.CanRecord)
                        return true;
                return false;
            }
        }

        /// <summary>
        /// What the recording follows from, rather than what it can be set to.
        ///
        /// Both used to be settings and neither was a choice: the fastest a recording can go is the
        /// rate the camera delivers, and the frame size is the size it delivers. Saying so once is
        /// more use than two boxes whose only honest values are these.
        /// </summary>
        public string RecordingSourceText
        {
            get
            {
                return TryAcquisition(out int w, out int h, out _, out double fps)
                    ? $"{w}x{h} at {fps:F3} fps, every frame"
                    : AnyCameraPresent ? "every frame, at the acquisition size" : "no camera";
            }
        }

        /// <summary>
        /// The first present camera's frame shape and rate, which is what the whole-app recording
        /// settings are described against. Channels can differ in principle; the settings window
        /// speaks about the recording in general, so it takes the first one that exists.
        /// </summary>
        private bool TryAcquisition(out int width, out int height, out int bands, out double fps)
        {
            width = height = 0; bands = 3; fps = 0.0;
            foreach (CameraChannel c in _manager.Channels)
            {
                if (!c.CameraPresent) continue;
                fps = c.DetectionFps;
                return c.TryGetFrameShape(out width, out height, out bands) && fps > 1.0;
            }
            return false;
        }

        private bool AnyCameraPresent
        {
            get
            {
                foreach (CameraChannel c in _manager.Channels)
                    if (c.CameraPresent) return true;
                return false;
            }
        }

        // ----- Detection: which faults the rig watches for -----

        /// <summary>
        /// Every anomaly kind, with its checkbox. App-wide, which is where the switch belongs: the
        /// thresholds under each camera had to be measured per optical path, but what the rig is
        /// looking for is one policy.
        ///
        /// Built once and held, so the checkboxes keep their bindings.
        /// </summary>
        public IReadOnlyList<AnomalyKindToggle> DetectionKinds =>
            _detectionKinds ??= AnomalyKindToggle.BuildFor(Output);

        private IReadOnlyList<AnomalyKindToggle> _detectionKinds;

        // ----- Encoding: what a session recording does to the pixels -----

        public bool EncodingIsH264
        {
            get => Output.RecordingEncoding == VideoEncoding.H264;
            set { if (value) SetEncoding(VideoEncoding.H264); }
        }

        public bool EncodingIsLossless
        {
            get => Output.RecordingEncoding == VideoEncoding.Lossless;
            set { if (value) SetEncoding(VideoEncoding.Lossless); }
        }

        public bool EncodingIsUncompressed
        {
            get => Output.RecordingEncoding == VideoEncoding.Uncompressed;
            set { if (value) SetEncoding(VideoEncoding.Uncompressed); }
        }

        /// <summary>
        /// What the chosen encoding writes, per minute, at the geometry actually being acquired.
        ///
        /// Shown because the three options are three orders of magnitude apart and nothing else on
        /// screen would say so: the H.264 clips from the 2026-09-10 run were 188-261 kb/s, while
        /// uncompressed at the same geometry is 295 MB/s. An operator picking the third radio
        /// deserves to see 18 GB/min before the disk fills, not after.
        /// </summary>
        public string RecordingCostText
        {
            get
            {
                VideoEncoding e = Output.RecordingEncoding;
                if (!TryAcquisition(out int w, out int h, out int bands, out double fps))
                    return VideoCodecs.IsLossless(e)
                        ? "bit-exact, and large - the size depends on the acquisition"
                        : "compressed, and small";

                string size = VideoCodecs.SizePerMinute(e, w, h, bands, fps);
                switch (e)
                {
                    case VideoEncoding.Lossless:
                        return $"{VideoCodecs.Name(e, bands)}, bit-exact - {size}, "
                             + "and real frames measured 4.2x smaller than that";
                    case VideoEncoding.Uncompressed:
                        return $"rawvideo, bit-exact - {size}, whatever the picture is";
                    default:
                        return "libx264 CRF 23, lossy - small, and the deviation cannot be "
                             + "measured again from it";
                }
            }
        }

        private void SetEncoding(VideoEncoding encoding)
        {
            if (Output.RecordingEncoding == encoding) return;
            Output.RecordingEncoding = encoding;   // persists
            RaiseChanged(nameof(EncodingIsH264));
            RaiseChanged(nameof(EncodingIsLossless));
            RaiseChanged(nameof(EncodingIsUncompressed));
            RaiseChanged(nameof(RecordingCostText));
            MilErrorLog.Note($"settings: recording encoding set to {encoding}"
                           + $" ({VideoCodecs.Name(encoding, 3)}, .{VideoCodecs.Extension(encoding)})"
                           + " - takes effect on the next Rec");
        }

        /// <summary>
        /// Whether to show the backend row at all.
        ///
        /// Hidden while the MIL sink does not exist, because then the row is a radio group with one
        /// option: it says "ffmpeg" and offers nothing. It comes back by itself the moment the
        /// supplier's code makes Readiness anything other than NotImplemented, which is exactly
        /// when there is a second answer to give - and the preference stays in settings.json
        /// meanwhile, so nothing is lost by not showing it.
        ///
        /// Except when nothing can record at all. Then the row carries the only explanation of why
        /// the Rec buttons are dead, and hiding it would leave an encoding choice on screen for a
        /// recording that cannot start.
        /// </summary>
        public bool SinkRowVisible =>
            VideoSinkFactory.Readiness(out _) != MilReadiness.NotImplemented ||
            EffectiveSink() == SinkChoice.None;

        /// <summary>Raised on the UI thread after every stats tick, so the view can redraw.</summary>
        public event Action StatsRefreshed;

        public RelayCommand StartAllCommand { get; }
        public RelayCommand StopAllCommand { get; }

        private void StartAll()
        {
            _manager.StartAll();
        }

        private void StopAll()
        {
            _manager.StopAll();
            foreach (var channel in _manager.Channels)
                channel.RefreshStats();
            RaiseChanged(nameof(AnyRecording));
        }

        /// <summary>
        /// Copies every capture setting group (exposure, acq rate, trigger, white balance) from
        /// <paramref name="source"/> onto all other present cameras that support each feature.
        /// </summary>
        public void ApplyAllSettings(CameraChannel source)
        {
            if (source == null) return;
            foreach (CameraSettingKind kind in Enum.GetValues(typeof(CameraSettingKind)))
                ApplyToAll(source, kind);
        }

        /// <summary>
        /// Copies one settings group from <paramref name="source"/> onto every other present
        /// camera that supports it (applying it to the hardware). Returns the number updated.
        /// </summary>
        public int ApplyToAll(CameraChannel source, CameraSettingKind kind)
        {
            if (source == null) return 0;
            int count = 0;
            foreach (var channel in _manager.Channels)
            {
                if (channel == source || !channel.CameraPresent) continue;
                if (ApplySettingFrom(source, channel, kind)) count++;
            }
            return count;
        }

        private static bool ApplySettingFrom(CameraChannel src, CameraChannel dst, CameraSettingKind kind)
        {
            switch (kind)
            {
                case CameraSettingKind.Exposure:
                    if (!dst.SupportsExposure) return false;
                    if (dst.SupportsExposureAuto) dst.ExposureAuto = src.ExposureAuto;
                    if (!src.ExposureAuto)
                    {
                        dst.ExposureInput = src.ExposureInput;
                        dst.ApplyExposure();
                    }
                    return true;

                case CameraSettingKind.AcqRate:
                    if (!dst.SupportsAcqRate) return false;
                    if (dst.SupportsAcqRateEnable) dst.AcqRateEnabled = src.AcqRateEnabled;
                    if (dst.CanSetAcqRate)
                    {
                        dst.AcqRateInput = src.AcqRateInput;
                        dst.ApplyAcqRate();
                    }
                    return true;

                case CameraSettingKind.Trigger:
                    if (!dst.SupportsTrigger) return false;
                    dst.TriggerOn = src.TriggerOn;
                    if (!string.IsNullOrEmpty(src.SelectedTriggerSource))
                        dst.SelectedTriggerSource = src.SelectedTriggerSource;
                    return true;

                case CameraSettingKind.WhiteBalance:
                    if (!dst.SupportsWhiteBalance) return false;
                    dst.WhiteBalanceAuto = src.WhiteBalanceAuto;
                    if (!src.WhiteBalanceAuto)
                    {
                        dst.RedRatioInput = src.RedRatioInput;
                        dst.BlueRatioInput = src.BlueRatioInput;
                        dst.ApplyBalanceRatios();
                    }
                    return true;
            }
            return false;
        }







        /// <summary>Starts recording on all grabbing cameras, or stops all if any are recording.</summary>
        public void ToggleRecordAll()
        {
            bool stop = AnyRecording;
            foreach (var channel in _manager.Channels)
            {
                if (stop)
                    channel.StopRecording();
                else if (channel.IsGrabbing)
                    channel.StartRecording();
            }
            RaiseChanged(nameof(AnyRecording));
        }

        /// <summary>Stops the timer; called on window close before freeing MIL resources.</summary>
        public void Shutdown()
        {
            _statsTimer.Stop();
            // After the timer, so no tick can reopen the file behind us.
            StopMeasurementLog();
        }

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;
        private void RaiseChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        #endregion
    }
}
