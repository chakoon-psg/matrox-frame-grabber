using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Matrox.MatroxImagingLibrary.WPF;
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

        private MILWPFDisplay _display;

        public CameraPaneView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private CameraChannel Channel => DataContext as CameraChannel;

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Create the MIL display control only once a channel with a valid display id is
            // bound. Every channel (even "no camera") allocates a display, so this is safe.
            if (_display == null && Channel != null && Channel.DisplayId != Matrox.MatroxImagingLibrary.MIL.M_NULL)
            {
                _display = new MILWPFDisplay { DisplayId = Channel.DisplayId };
                ViewBorder.Child = _display;
                Channel.FitToWindow();
            }
        }

        // ----- View sizing / interaction -----

        private void ViewBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Re-fit the whole image whenever the tile changes size.
            Channel?.FitToWindow();
        }

        private void ViewBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                RequestFullscreen();
                e.Handled = true;
            }
        }

        private void RequestFullscreen()
        {
            var channel = Channel;
            if (channel != null && channel.CameraPresent)
                FullscreenRequested?.Invoke(this, channel);
        }

        // ----- Toolbar -----

        private void Start_Click(object sender, RoutedEventArgs e) => Channel?.StartGrab();
        private void Stop_Click(object sender, RoutedEventArgs e) => Channel?.StopGrab();
        private void Fit_Click(object sender, RoutedEventArgs e) => Channel?.FitToWindow();
        private void OneToOne_Click(object sender, RoutedEventArgs e) => Channel?.ZoomActual();
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

        private void ApplyExposure_Click(object sender, RoutedEventArgs e)
        {
            var channel = Channel;
            if (channel == null) return;
            if (!channel.ApplyExposure())
                MessageBox.Show("Failed to set exposure (value out of range or feature unavailable).",
                    channel.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
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
