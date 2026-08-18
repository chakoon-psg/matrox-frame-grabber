using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Threading;
using MatroxFrameGrabber.Infrastructure;
using MatroxFrameGrabber.Mil;

namespace MatroxFrameGrabber.ViewModels
{
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
            SetAllAcqRateCommand = new RelayCommand(SetAllAcqRate);

            // Run the stats timer for the whole session so per-pane Start also updates fps/status.
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statsTimer.Tick += (s, e) =>
            {
                foreach (var channel in _manager.Channels)
                    channel.RefreshStats();
                RaiseChanged(nameof(AnyRecording));
                RaiseChanged(nameof(AnyRawRecording));
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

        /// <summary>True if at least one camera supports recording (MIL compression licensed).</summary>
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

        /// <summary>True if at least one camera exposes AcquisitionFrameRate (enables "Set all").</summary>
        public bool AnyCanAcqRate
        {
            get
            {
                foreach (var channel in _manager.Channels)
                    if (channel.SupportsAcqRate)
                        return true;
                return false;
            }
        }

        private string _globalAcqRate = "60";
        /// <summary>The fps value applied to every camera by "Set all".</summary>
        public string GlobalAcqRate
        {
            get => _globalAcqRate;
            set { _globalAcqRate = value; RaiseChanged(nameof(GlobalAcqRate)); }
        }

        public RelayCommand StartAllCommand { get; }
        public RelayCommand StopAllCommand { get; }
        public RelayCommand SetAllAcqRateCommand { get; }

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

        /// <summary>Applies <see cref="GlobalAcqRate"/> fps to every camera that supports it.</summary>
        private void SetAllAcqRate()
        {
            foreach (var channel in _manager.Channels)
            {
                if (!channel.SupportsAcqRate)
                    continue;
                channel.AcqRateInput = _globalAcqRate;
                channel.ApplyAcqRate();
            }
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
