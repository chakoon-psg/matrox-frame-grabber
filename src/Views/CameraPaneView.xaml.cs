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

        // ----- Settings strip -----

        /// <summary>
        /// Enter in a settings field applies that row, so the operator does not have to reach for
        /// the Apply button. The row is identified by the box's Tag, set in XAML — keeping the
        /// mapping in the markup next to the field it belongs to.
        /// </summary>
        private void SettingsField_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (!(sender is FrameworkElement box) || !(box.Tag is string row)) return;

            switch (row)
            {
                case "exposure": ApplyExposure_Click(sender, e); break;
                case "acqrate":  ApplyAcqRate_Click(sender, e); break;
                case "balance":  ApplyBalance_Click(sender, e); break;
                case "roi":      ApplyRoi_Click(sender, e); break;
                default: return;
            }
            e.Handled = true;
        }

        private void ApplyExposure_Click(object sender, RoutedEventArgs e)
        {
            var channel = Channel;
            if (channel == null) return;
            if (!channel.ApplyExposure())
                MessageBox.Show("Failed to set exposure (value out of range or feature unavailable).",
                    channel.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void ApplyAcqRate_Click(object sender, RoutedEventArgs e)
        {
            var channel = Channel;
            if (channel == null) return;
            if (!channel.ApplyAcqRate())
                MessageBox.Show("Failed to set acquisition rate (value out of range or feature unavailable).",
                    channel.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void Decimation_Changed(object sender, SelectionChangedEventArgs e)
        {
            var channel = Channel;
            if (channel == null) return;
            if (!(sender is ComboBox combo) || !(combo.SelectedItem is int factor)) return;
            if (factor == channel.Decimation) return;   // echo of our own OneWay binding
            // ApplyDecimation returns false when the camera did not take the value. Say so rather
            // than leaving the combo asserting a factor the hardware refused — this camera returns
            // success for geometry writes it ignores, which is why the check exists at all.
            if (!channel.ApplyDecimation(factor))
            {
                // Put the combo back to what the camera actually has. A user selection writes a
                // local value, which detaches the OneWay binding — so without this the combo would
                // keep asserting a factor the hardware refused, which is the exact silent lie this
                // handler exists to prevent. The echo guard above makes the re-entrant
                // SelectionChanged a no-op, so this cannot loop.
                combo.SelectedItem = channel.Decimation;
                MessageBox.Show(
                    "디시메이션을 적용하지 못했습니다. 카메라가 값을 받아들이지 않았습니다.",
                    channel.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ApplyRoi_Click(object sender, RoutedEventArgs e)
        {
            var channel = Channel;
            if (channel == null) return;
            if (!channel.ApplyAnalysisRoiFromInputs())
                MessageBox.Show("ROI를 적용하지 못했습니다. 네 값이 모두 정수여야 합니다.",
                    channel.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void ClearRoi_Click(object sender, RoutedEventArgs e) => Channel?.ClearAnalysisRoi();

        // Copy this camera's capture settings (exposure / acq rate / trigger / WB) to every other camera.
        private void ApplyAll_Click(object sender, RoutedEventArgs e) =>
            ApplyToAllRequested?.Invoke(this, EventArgs.Empty);

        private System.Windows.Threading.DispatcherTimer _rawTimer;

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            var channel = Channel;
            if (channel == null) return;

            // Stopping ends any in-progress recording (irreversible) — confirm, but only while recording.
            if ((channel.IsRecording || channel.IsRawRecording) &&
                MessageBox.Show("이 카메라가 녹화 중입니다. 중지하면 녹화가 종료됩니다. 계속할까요?",
                    channel.Name, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            StopRawTimer();
            if (channel.IsRawRecording) channel.StopRawRecording();
            channel.StopGrab();   // also stops a color recording
        }

        private void RawRecord_Click(object sender, RoutedEventArgs e)
        {
            var channel = Channel;
            if (channel == null) return;

            if (channel.IsRawRecording)
            {
                StopRawTimer();
                channel.StopRawRecording();
                return;
            }

            if (!channel.StartRawRecording(out string error))
            {
                MessageBox.Show(error ?? "Failed to start RAW recording.", channel.Name,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int seconds = channel.Output?.RawDurationSeconds ?? 0;
            if (seconds > 0)
            {
                _rawTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(seconds)
                };
                _rawTimer.Tick += (s, a) => { StopRawTimer(); Channel?.StopRawRecording(); };
                _rawTimer.Start();
            }
        }

        private void StopRawTimer()
        {
            if (_rawTimer != null) { _rawTimer.Stop(); _rawTimer = null; }
        }

        private void SoftTrigger_Click(object sender, RoutedEventArgs e) => Channel?.FireSoftwareTrigger();

        private void WhiteBalanceOnce_Click(object sender, RoutedEventArgs e) => Channel?.WhiteBalanceOnce();

        private void ApplyBalance_Click(object sender, RoutedEventArgs e)
        {
            var channel = Channel;
            if (channel == null) return;
            if (!channel.ApplyBalanceRatios())
                MessageBox.Show("Failed to apply white-balance ratios.", channel.Name,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void FeatureBrowser_Click(object sender, RoutedEventArgs e) => Channel?.OpenFeatureBrowser();

        private void LoadDcf_Click(object sender, RoutedEventArgs e)
        {
            var channel = Channel;
            if (channel == null) return;

            var dialog = new OpenFileDialog
            {
                Title = $"{channel.Name}: select a DCF (camera configuration) file",
                Filter = "Matrox DCF (*.dcf)|*.dcf|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true || !File.Exists(dialog.FileName))
                return;

            if (!channel.ReloadWithDcf(dialog.FileName))
                MessageBox.Show("Reallocated with the selected DCF, but no camera was detected.",
                    channel.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
