using System;
using System.Collections.Generic;
using System.Windows.Threading;
using MatroxFrameGrabber.Infrastructure;
using MatroxFrameGrabber.Mil;

namespace MatroxFrameGrabber.ViewModels
{
    /// <summary>
    /// Binds the camera channels and the Start/Stop commands to the main window and drives
    /// a UI-thread timer that refreshes per-channel live statistics.
    /// </summary>
    public class MainViewModel
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
            };
        }

        /// <summary>The three camera channels, bound by index in the XAML.</summary>
        public IReadOnlyList<CameraChannel> Channels => _manager.Channels;

        /// <summary>Board / system summary shown in the header.</summary>
        public string SystemStatus =>
            $"System: {_manager.AllocatedSystemDescriptor}   Digitizers: {_manager.DigitizerCount}";

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
    }
}
