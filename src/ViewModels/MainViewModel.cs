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

            // Run the stats timer for the whole session so per-pane Start also updates fps/status.
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statsTimer.Tick += (s, e) =>
            {
                foreach (var channel in _manager.Channels)
                    channel.RefreshStats();
                RaiseChanged(nameof(AnyRecording));
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
