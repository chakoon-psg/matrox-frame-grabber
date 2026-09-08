using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MatroxFrameGrabber.Mil;
using Microsoft.Win32;

namespace MatroxFrameGrabber.Views
{
    /// <summary>
    /// One camera's settings, hosted either inside its pane's Settings expander or in a window of
    /// its own. Every handler here was moved out of <see cref="CameraPaneView"/> unchanged; the
    /// pane keeps only what belongs beside the live image.
    ///
    /// The control holds no state. Its <see cref="FrameworkElement.DataContext"/> is the
    /// <see cref="CameraChannel"/>, so the pane's copy and a popped-out window are two views of
    /// one view-model rather than the same control moved between parents — which means no visual
    /// tree surgery, and no interaction with the MIL display control, which lives elsewhere in the
    /// pane and is not part of this.
    /// </summary>
    public partial class CameraSettingsView : UserControl
    {
        public CameraSettingsView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Raised when this camera's capture settings should be copied to every other camera.
        /// Re-raised by the host so <see cref="MainWindow"/> keeps subscribing to the pane, whose
        /// event it already knows about, wherever this control happens to be shown.
        /// </summary>
        public event EventHandler ApplyToAllRequested;

        private CameraChannel Channel => DataContext as CameraChannel;

        /// <summary>
        /// Enter applies the row the field belongs to. The row is named by the field's Tag, so a
        /// field added to a row needs the tag and nothing else.
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
                case "detect":   ApplyThresholds_Click(sender, e); break;
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

        private void ApplyCalibration_Click(object sender, RoutedEventArgs e)
        {
            var channel = Channel;
            if (channel == null) return;
            if (!channel.ApplyCalibration())
                MessageBox.Show(
                    "제안할 값이 없습니다. 정상 패널 앞에서 충분히 긴 실행을 한 번 마쳐야 합니다.",
                    channel.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void ApplyThresholds_Click(object sender, RoutedEventArgs e)
        {
            var channel = Channel;
            if (channel == null) return;
            if (!channel.ApplyDetectionThresholds())
                MessageBox.Show("임계값을 적용하지 못했습니다. 두 값이 모두 숫자여야 합니다.",
                    channel.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void ApplyAll_Click(object sender, RoutedEventArgs e) =>
            ApplyToAllRequested?.Invoke(this, EventArgs.Empty);

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
