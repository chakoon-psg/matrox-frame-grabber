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
        private bool _isRunning;

        public MainViewModel(MilApplicationManager manager)
        {
            _manager = manager;

            StartAllCommand = new RelayCommand(StartAll, () => !_isRunning);
            StopAllCommand = new RelayCommand(StopAll, () => _isRunning);

            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statsTimer.Tick += (s, e) =>
            {
                foreach (var channel in _manager.Channels)
                    channel.RefreshStats();
                RaiseChanged(nameof(AnyRecording));
            };
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
            _isRunning = true;
            _statsTimer.Start();
            RefreshCommandStates();
        }

        private void StopAll()
        {
            _manager.StopAll();
            _isRunning = false;
            _statsTimer.Stop();
            foreach (var channel in _manager.Channels)
                channel.RefreshStats();
            RefreshCommandStates();
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

        private void RefreshCommandStates()
        {
            StartAllCommand.RaiseCanExecuteChanged();
            StopAllCommand.RaiseCanExecuteChanged();
        }

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;
        private void RaiseChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        #endregion
    }
}
