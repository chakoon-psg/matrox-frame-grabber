using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Matrox.MatroxImagingLibrary.WPF;
using MatroxFrameGrabber.Infrastructure;
using MatroxFrameGrabber.Mil;
using Microsoft.Win32;

namespace MatroxFrameGrabber.Views
{
    /// <summary>
    /// One camera pane: live view + per-camera controls (exposure, trigger, white balance,
    /// DCF) and view actions (fit, 1:1, snapshot, fullscreen). DataContext is a CameraChannel.
    /// </summary>
    public partial class CameraPaneView : UserControl
    {
        /// <summary>Raised when the user requests fullscreen for this pane's camera.</summary>
        public event EventHandler<CameraChannel> FullscreenRequested;

        /// <summary>Raised when the "apply to all" button is clicked (copy this camera's settings to all).</summary>
        public event EventHandler ApplyToAllRequested;

        private MILWPFDisplay _display;

        private RoiEditSurface _roi;

        public CameraPaneView()
        {
            InitializeComponent();

            // Re-raised, so MainWindow goes on subscribing to the pane wherever the settings are
            // actually being shown - inline here, or in a window of their own.
            InlineSettings.ApplyToAllRequested += (s, e) => ApplyToAllRequested?.Invoke(this, EventArgs.Empty);
            // The rectangle and its handles are built in code so this pane and the fullscreen
            // overlay show the same thing without the visuals being declared twice.
            _roi = new RoiEditSurface(ViewBorder, ViewContentGrid, () => Channel, "pane");
            DataContextChanged += OnDataContextChanged;
        }

        private CameraChannel Channel => DataContext as CameraChannel;

        /// <summary>
        /// Hands this pane's MIL display control to the fullscreen overlay.
        ///
        /// The control moves rather than a second one being created for the same DisplayId. MIL
        /// keeps one zoom per display, so two controls bound to one display leave "fit to window"
        /// with no way to know which window is meant: the pane came back from fullscreen still
        /// scaled to the overlay — measured at zoom 1.011 where its own fit is 0.30 — and drew its
        /// ROI rectangle off the surface as a result.
        /// </summary>
        public MILWPFDisplay DetachDisplay()
        {
            if (_display == null)
                return null;
            ViewContentGrid.Children.Remove(_display);
            return _display;
        }

        /// <summary>
        /// Takes the display control back. Does not refit: the caller does that once layout has
        /// given this pane its size again, or the fit would be computed against nothing.
        /// </summary>
        public void ReattachDisplay()
        {
            if (_display == null || ViewContentGrid.Children.Contains(_display))
                return;
            ViewContentGrid.Children.Insert(0, _display);
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Create the MIL display control only once a channel with a valid display id is
            // bound. Every channel (even "no camera") allocates a display, so this is safe.
            if (_display == null && Channel != null && Channel.DisplayId != Matrox.MatroxImagingLibrary.MIL.M_NULL)
            {
                _display = new MILWPFDisplay { DisplayId = Channel.DisplayId };
                // Index 0 so the ROI rectangle and its handles, added by RoiEditSurface, stay
                // above the live image in z-order.
                ViewContentGrid.Children.Insert(0, _display);
                Channel.FitToWindow();
            }
        }

        /// <summary>
        /// Positions the analysis rectangle over the live image. Called from the window's 500 ms
        /// stats tick because MIL handles zoom and pan natively and raises no event to follow.
        /// </summary>
        public void RefreshRoiOverlay() => _roi?.Refresh();

        // ----- View sizing / interaction -----

        private void ViewBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Re-fit on resize, but not over a zoom or pan the operator chose.
            Channel?.FitToWindowIfUntouched();
        }

        /// <summary>Turns edit mode on and off with the ⬚ toggle, and shows or hides the handles.</summary>
        private void RoiSelectToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_roi == null) return;
            if (RoiSelectToggle.IsChecked == true)
            {
                _roi.EditMode = true;
                _roi.Refresh();
            }
            else
            {
                _roi.ExitEditMode();
            }
        }

        private void ViewBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // The edit surface takes the press while the toggle is on, which also keeps it from
            // reaching the MIL control below and starting a pan.
            if (_roi != null && _roi.TryBeginDrag(e))
            {
                e.Handled = true;
                return;
            }

            if (e.ClickCount == 2)
            {
                RequestFullscreen();
                e.Handled = true;
            }
        }

        private void ViewBorder_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_roi == null) return;
            bool wasDragging = _roi.IsDragging;
            _roi.ContinueDrag(e);
            if (wasDragging)
                e.Handled = true;
        }

        private void ViewBorder_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_roi == null || !_roi.IsDragging) return;
            _roi.EndDrag(e);
            e.Handled = true;
        }


        private void RequestFullscreen()
        {
            var channel = Channel;
            if (channel != null && channel.CameraPresent)
                FullscreenRequested?.Invoke(this, channel);
        }

        // ----- Toolbar (Start/Stop/Fit/1:1 are ICommands on the channel; these need view context) -----

        private void Fullscreen_Click(object sender, RoutedEventArgs e) => RequestFullscreen();

        private void Snapshot_Click(object sender, RoutedEventArgs e)
        {
            var channel = Channel;
            if (channel == null) return;

            string path = channel.SaveSnapshotToOutput();
            if (path == null)
                MessageBox.Show("Failed to save snapshot.", channel.Name,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void Record_Click(object sender, RoutedEventArgs e)
        {
            var channel = Channel;
            if (channel == null) return;

            if (!channel.IsRecording && !channel.IsGrabbing)
            {
                MessageBox.Show("Start the camera before recording.", channel.Name,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool wasRecording = channel.IsRecording;
            channel.ToggleRecording();
            if (!wasRecording && !channel.IsRecording)
            {
                string reason = channel.LastRecordError ?? "ffmpeg not found or failed to launch.";
                MessageBox.Show($"Failed to start recording:\n{reason}",
                    channel.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            var channel = Channel;
            if (channel == null) return;

            // Stopping ends any in-progress recording (irreversible) — confirm, but only while recording.
            if (channel.IsRecording &&
                MessageBox.Show("이 카메라가 녹화 중입니다. 중지하면 녹화가 종료됩니다. 계속할까요?",
                    channel.Name, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            channel.StopGrab();   // also stops the recording
        }

        private CameraSettingsWindow _settingsWindow;

        /// <summary>
        /// Opens this camera's settings in a window of its own, and closes the inline copy.
        ///
        /// The window exists because the pane is too narrow for one of the rows: the Calib text
        /// runs to about 600 px against a pane's 360, so inline it is clipped and has to be read
        /// from a tooltip. Widening the pane is not available - four of them share the window.
        ///
        /// One window per pane. A second click focuses the one already open rather than opening
        /// another view of the same channel, which would leave two sets of fields disagreeing about
        /// what had been typed but not yet applied.
        /// </summary>
        private void PopOut_Click(object sender, RoutedEventArgs e)
        {
            // The header sits inside a ToggleButton, so without this the expander toggles too.
            e.Handled = true;

            var channel = Channel;
            if (channel == null) return;

            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new CameraSettingsWindow(channel, Window.GetWindow(this));
            _settingsWindow.ApplyToAllRequested += (s, args) => ApplyToAllRequested?.Invoke(this, EventArgs.Empty);
            _settingsWindow.Closed += (s, args) =>
            {
                _settingsWindow = null;
                SettingsExpander.IsEnabled = true;
            };

            // The inline copy is closed and disabled while the window is up, for the same reason
            // only one window is allowed.
            SettingsExpander.IsExpanded = false;
            SettingsExpander.IsEnabled = false;
            _settingsWindow.Show();
        }

    }
}
