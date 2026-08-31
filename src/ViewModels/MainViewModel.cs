using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Threading;
using MatroxFrameGrabber.Infrastructure;
using MatroxFrameGrabber.Mil;

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

            // Run the stats timer for the whole session so per-pane Start also updates fps/status.
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statsTimer.Tick += (s, e) =>
            {
                foreach (var channel in _manager.Channels)
                    channel.RefreshStats();
                RaiseChanged(nameof(AnyRecording));
                RaiseChanged(nameof(AnyRawRecording));
                StatsRefreshed?.Invoke();
            };
            _statsTimer.Start();
        }

        /// <summary>The camera channels, bound by index in the XAML.</summary>
        public IReadOnlyList<CameraChannel> Channels => _manager.Channels;

        /// <summary>App-wide output folder + resolution settings.</summary>
        public OutputSettings Output => _manager.Output;

        /// <summary>Resolution presets shown in the toolbar combo.</summary>
        public Array ResolutionOptions => Enum.GetValues(typeof(OutputResolution));

        /// <summary>Board / system summary shown in the header.</summary>
        public string SystemStatus =>
            $"System: {_manager.AllocatedSystemDescriptor}   Digitizers: {_manager.DigitizerCount}";

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

        /// <summary>True if at least one camera supports recording (i.e. ffmpeg was found — recording never uses a MIL compression licence; see docs/adr/).</summary>
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
        /// The ffmpeg.exe actually resolved for this run, for the recording settings popup.
        /// Bound once at load: the configured path has no editor, so this cannot change while
        /// the window is open.
        /// </summary>
        public string FfmpegPathText
        {
            get
            {
                string path = FfmpegRecorder.ResolveFfmpegPath(Output.FfmpegPath);
                return string.IsNullOrEmpty(path)
                    ? "not found — recording disabled"
                    : path;
            }
        }

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
            StopRawAll();   // finalize any RAW recordings (+ cancel the auto-stop timer) first
            _manager.StopAll();
            foreach (var channel in _manager.Channels)
                channel.RefreshStats();
            RaiseChanged(nameof(AnyRecording));
            RaiseChanged(nameof(AnyRawRecording));
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

        /// <summary>True if any camera is currently doing a lossless RAW recording.</summary>
        public bool AnyRawRecording
        {
            get
            {
                foreach (var channel in _manager.Channels)
                    if (channel.IsRawRecording)
                        return true;
                return false;
            }
        }

        /// <summary>Auto-stop duration (seconds) for RAW recording; 0 = manual. Persisted via Output.</summary>
        public string RawSeconds
        {
            get => Output.RawDurationSeconds.ToString();
            set
            {
                if (int.TryParse(value, out int s))
                    Output.RawDurationSeconds = s;
                RaiseChanged(nameof(RawSeconds));
            }
        }

        /// <summary>Length of each RAW MP4 segment (seconds). Persisted via Output.</summary>
        public string RawSegSeconds
        {
            get => Output.RawSegmentSeconds.ToString();
            set
            {
                if (int.TryParse(value, out int s))
                    Output.RawSegmentSeconds = s;
                RaiseChanged(nameof(RawSegSeconds));
            }
        }

        private DispatcherTimer _rawAllTimer;

        /// <summary>
        /// Starts a lossless RAW recording on every present camera, or stops all if any are.
        /// Returns a combined error message for channels that failed to start (null if all OK).
        /// </summary>
        public string ToggleRawAll()
        {
            if (AnyRawRecording)
            {
                StopRawAll();
                return null;
            }

            var errors = new List<string>();
            foreach (var channel in _manager.Channels)
                if (channel.CameraPresent && !channel.StartRawRecording(out string err))
                    errors.Add($"{channel.Name}: {err}");
            RaiseChanged(nameof(AnyRawRecording));

            // Only arm the auto-stop timer if at least one camera actually started.
            int seconds = Output.RawDurationSeconds;
            if (seconds > 0 && AnyRawRecording)
            {
                _rawAllTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                _rawAllTimer.Tick += (s, e) => StopRawAll();
                _rawAllTimer.Start();
            }

            return errors.Count > 0 ? string.Join("\n", errors) : null;
        }

        private void StopRawAll()
        {
            if (_rawAllTimer != null) { _rawAllTimer.Stop(); _rawAllTimer = null; }
            foreach (var channel in _manager.Channels)
                if (channel.IsRawRecording)
                    channel.StopRawRecording();
            RaiseChanged(nameof(AnyRawRecording));
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
        }

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;
        private void RaiseChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        #endregion
    }
}
