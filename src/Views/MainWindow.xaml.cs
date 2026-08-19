using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Matrox.MatroxImagingLibrary.WPF;
using MatroxFrameGrabber.Infrastructure;
using MatroxFrameGrabber.Mil;
using MatroxFrameGrabber.ViewModels;

namespace MatroxFrameGrabber.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml. Owns the MIL lifecycle and the fullscreen
    /// overlay for a single selected camera.
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int VK_ESCAPE = 0x1B;

        private MilApplicationManager _manager;
        private MainViewModel _viewModel;
        private CameraChannel _fullscreenChannel;
        private MILWPFDisplay _fullscreenDisplay;
        private bool _escHookInstalled;
        private string _initError;

        public MainWindow()
        {
            // Allocate MIL and set the DataContext BEFORE building the visual tree so the
            // MILWPFDisplay controls are constructed with a valid DisplayId (an unbound/zero
            // display id makes MIL raise an error dialog).
            try
            {
                _manager = new MilApplicationManager();
                _manager.Allocate();

                // Surface unexpected recording stops (e.g. ffmpeg died) and camera loss.
                foreach (var ch in _manager.Channels)
                {
                    ch.RecordingFailed += OnRecordingFailed;
                    ch.CameraLost += OnCameraLost;
                    ch.GrabFailed += OnGrabFailed;
                    ch.RawRecordingFinished += OnRawRecordingFinished;
                }

                _viewModel = new MainViewModel(_manager);
                DataContext = _viewModel;
            }
            catch (Exception ex)
            {
                _initError = ex.Message;
            }

            InitializeComponent();

            if (_viewModel != null)
                ((MainViewModel)DataContext).StatsRefreshed += RedrawBrightness;
        }

        private void OnRecordingFailed(CameraChannel channel, string error)
        {
            MessageBox.Show($"Recording stopped: {error}", channel.Name,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void OnGrabFailed(CameraChannel channel, string error)
        {
            MessageBox.Show($"Could not start acquisition: {error}", channel.Name,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void OnCameraLost(CameraChannel channel)
        {
            MessageBox.Show("Camera was disconnected. Press Stop, reconnect it, then Start again.",
                channel.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            NativeMethods.UseImmersiveDarkTitleBar(this);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initError != null)
            {
                MessageBox.Show(_initError, "MIL Allocation Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_viewModel != null)
                _viewModel.StatsRefreshed -= RedrawBrightness;
            ExitFullscreen();
            RemoveEscHook();
            _viewModel?.Shutdown();
            DataContext = null;
            _manager?.Free();
            _manager = null;
        }

        // ----- Toolbar: output folder / recording -----

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null) return;
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select output folder for snapshots and recordings";
                dialog.UseDescriptionForTitle = true;
                if (Directory.Exists(_viewModel.Output.OutputFolder))
                    dialog.SelectedPath = _viewModel.Output.OutputFolder;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    _viewModel.Output.OutputFolder = dialog.SelectedPath;
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string folder = _viewModel?.Output.EnsureFolder();
                if (!string.IsNullOrEmpty(folder))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            catch
            {
                // ignore
            }
        }

        private void StopAll_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null) return;

            // Stopping all ends every in-progress recording (irreversible) — confirm, but only if recording.
            if ((_viewModel.AnyRecording || _viewModel.AnyRawRecording) &&
                MessageBox.Show("녹화 중인 카메라가 있습니다. 모두 중지하면 녹화가 종료됩니다. 계속할까요?",
                    "Stop All", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            _viewModel.StopAllCommand.Execute(null);
        }

        private void RecordAll_Click(object sender, RoutedEventArgs e)
        {
            _viewModel?.ToggleRecordAll();
        }

        private void RecSettings_Click(object sender, RoutedEventArgs e)
        {
            RecSettingsPopup.IsOpen = !RecSettingsPopup.IsOpen;
        }

        private void RawAll_Click(object sender, RoutedEventArgs e)
        {
            string errors = _viewModel?.ToggleRawAll();
            if (!string.IsNullOrEmpty(errors))
                MessageBox.Show("Some cameras could not start RAW recording:\n\n" + errors,
                    "RAW All", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Only surface RAW conversion FAILURES (success just leaves the .mp4 in the output folder).
        private void OnRawRecordingFinished(CameraChannel channel, bool ok, string message)
        {
            if (!ok)
                MessageBox.Show($"RAW recording could not be converted:\n{message}", channel.Name,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ----- Fullscreen -----

        private void Pane_FullscreenRequested(object sender, CameraChannel channel)
        {
            EnterFullscreen(channel);
        }

        // A pane's "apply to all" button: copy that camera's capture settings to every other camera.
        private void Pane_ApplyToAllRequested(object sender, EventArgs e)
        {
            if ((sender as CameraPaneView)?.DataContext is CameraChannel source)
                _viewModel?.ApplyAllSettings(source);
        }

        private void EnterFullscreen(CameraChannel channel)
        {
            if (channel == null || !channel.CameraPresent || _fullscreenChannel != null)
                return;

            _fullscreenChannel = channel;
            FullscreenTitle.Text = $"{channel.Name} — double-click or press ESC to exit";

            // Create the display control on demand, bound to the selected camera's display.
            _fullscreenDisplay = new MILWPFDisplay { DisplayId = channel.DisplayId };
            FullscreenBorder.Child = _fullscreenDisplay;

            MainContent.Visibility = Visibility.Collapsed;
            FullscreenOverlay.Visibility = Visibility.Visible;
            InstallEscHook();

            Dispatcher.BeginInvoke(new Action(() => _fullscreenChannel?.FitToWindow()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ExitFullscreen()
        {
            if (_fullscreenChannel == null)
                return;

            RemoveEscHook();
            FullscreenOverlay.Visibility = Visibility.Collapsed;
            FullscreenBorder.Child = null;           // drop the overlay display control
            _fullscreenDisplay = null;
            MainContent.Visibility = Visibility.Visible;

            var channel = _fullscreenChannel;
            _fullscreenChannel = null;

            Dispatcher.BeginInvoke(new Action(() => channel.FitToWindow()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ExitFullscreen_Click(object sender, RoutedEventArgs e) => ExitFullscreen();

        private void FullscreenBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (FullscreenOverlay.Visibility == Visibility.Visible)
                _fullscreenChannel?.FitToWindow();
        }

        private void FullscreenBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ExitFullscreen();
                e.Handled = true;
            }
        }

        // ----- ESC via the thread message pump -----
        // A MILWPFDisplay with M_KEYBOARD_USE enabled makes MIL subclass the top-level window
        // HWND and swallow key messages (including ESC) before WPF turns them into routed
        // events, so Window.PreviewKeyDown never fires. ThreadFilterMessage sees WM_KEYDOWN in
        // the dispatcher pump ahead of every WndProc, so it reliably catches ESC.

        private void InstallEscHook()
        {
            if (_escHookInstalled) return;
            ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
            _escHookInstalled = true;
        }

        private void RemoveEscHook()
        {
            if (!_escHookInstalled) return;
            ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
            _escHookInstalled = false;
        }

        private void OnThreadFilterMessage(ref MSG msg, ref bool handled)
        {
            if ((msg.message == WM_KEYDOWN || msg.message == WM_SYSKEYDOWN)
                && msg.wParam.ToInt32() == VK_ESCAPE
                && FullscreenOverlay.Visibility == Visibility.Visible)
            {
                ExitFullscreen();   // also removes this hook
                handled = true;     // swallow ESC so MIL doesn't act on it
            }
        }

        // ----- Brightness strip -----

        /// <summary>Clipping above this share of sampled pixels is called out in the legend.</summary>
        private const double ClipWarnPercent = 1.0;

        private static readonly string[] ChannelBrushKeys = { "Ch0Brush", "Ch1Brush", "Ch2Brush", "Ch3Brush" };
        private readonly Polyline[] _brightnessLines = new Polyline[4];
        private readonly BrightnessSample[] _sampleScratch = new BrightnessSample[BrightnessHistory.Capacity];

        // Static axis chrome (gridlines + labels), built once and repositioned on every redraw as
        // the canvas resizes. Recreating these every tick would grow the visual tree without bound.
        private static readonly double[] GridLumaLevels = { 64, 128, 192 };
        private readonly Line[] _brightnessGridLines = new Line[GridLumaLevels.Length];
        private readonly TextBlock[] _yAxisLabels = new TextBlock[3];   // "255" / "128" / "0"
        private TextBlock _xAxisStartLabel, _xAxisEndLabel;             // "-120s" / "now"

        /// <summary>
        /// Redraws the brightness strip. The vertical axis is pinned to 0-255 rather than scaled to
        /// the data: an auto-scaled axis hides the slow drift the graph exists to reveal.
        /// </summary>
        private void RedrawBrightness()
        {
            var vm = DataContext as MainViewModel;
            // Cleared before the early-return guard: a collapsed strip, or one whose canvas hasn't
            // been laid out yet, must never keep showing labels from a previous state.
            BrightnessLegend.Children.Clear();
            if (vm == null || !vm.ShowBrightness || BrightnessCanvas.ActualWidth <= 0)
                return;

            double w = BrightnessCanvas.ActualWidth;
            double h = BrightnessCanvas.ActualHeight;

            EnsureAxisElements();
            RepositionAxisElements(w, h);

            for (int i = 0; i < _brightnessLines.Length; i++)
            {
                if (_brightnessLines[i] == null)
                {
                    _brightnessLines[i] = new Polyline
                    {
                        Stroke = (Brush)FindResource(ChannelBrushKeys[i]),
                        StrokeThickness = 1.5
                    };
                    BrightnessCanvas.Children.Add(_brightnessLines[i]);
                }
            }

            for (int i = 0; i < vm.Channels.Count && i < _brightnessLines.Length; i++)
            {
                var channel = vm.Channels[i];
                // A channel with no camera, or one that is stopped, has no valid display buffer —
                // drawing a flat zero for it would read as "this camera is completely dark". A
                // stopped channel also must not keep asserting its last (now stale) reading, so
                // its line is explicitly emptied rather than merely left unassigned this tick.
                if (!channel.IsGrabbing || !channel.Brightness.HasData)
                {
                    _brightnessLines[i].Points = new PointCollection();
                    continue;
                }

                // Points are built into a fresh collection and assigned once, instead of appending
                // to the live PointCollection already bound to the Polyline — the latter fires a
                // change notification per point (~960/tick across 4 channels), which is exactly the
                // kind of avoidable cost this branch exists to eliminate.
                int n = channel.Brightness.CopyTo(_sampleScratch);
                var points = new PointCollection(n);
                for (int p = 0; p < n; p++)
                {
                    // Right-aligned: "now" is always the right edge, so every running channel's
                    // samples line up on the same shared time axis regardless of when it started
                    // (a channel with fewer samples simply has a shorter line, growing from the right).
                    double x = w - (n - 1 - p) * w / (BrightnessHistory.Capacity - 1);
                    double y = h - (h * _sampleScratch[p].Luma / 255.0);
                    points.Add(new Point(x, y));
                }
                _brightnessLines[i].Points = points;

                BrightnessSample latest = channel.Brightness.Latest;
                var label = new TextBlock
                {
                    Text = $"{channel.Name}  {latest.Luma:F0}   clip {latest.ClippedPct:F1}%  blk {latest.BlackPct:F1}%",
                    Margin = new Thickness(0, 0, 14, 0),
                    Foreground = latest.ClippedPct >= ClipWarnPercent
                        ? (Brush)FindResource("RecBrush")
                        : (Brush)FindResource(ChannelBrushKeys[i])
                };
                BrightnessLegend.Children.Add(label);
            }

            // Diagnostic tooltip on the toggle itself: makes the 500 ms budget check (spec
            // verification 3) and a permanently-failing channel (F6) both readable at a glance,
            // instead of requiring a debugger.
            var diag = new StringBuilder();
            for (int i = 0; i < vm.Channels.Count; i++)
            {
                if (diag.Length > 0) diag.Append("  ");
                var channel = vm.Channels[i];
                diag.Append($"ch{i} ");
                diag.Append(channel.BrightnessFailures > 0
                    ? $"FAIL x{channel.BrightnessFailures}"
                    : $"{channel.LastBrightnessSampleMs:F1} ms");
            }
            BrightnessToggle.ToolTip = diag.ToString();
        }

        /// <summary>Creates the gridlines/labels once (idempotent). Added before the data polylines
        /// created in <see cref="RedrawBrightness"/> so the gridlines render behind the data.</summary>
        private void EnsureAxisElements()
        {
            if (_brightnessGridLines[0] != null)
                return;

            var gridBrush = (Brush)FindResource("BorderBrushColor");
            for (int i = 0; i < GridLumaLevels.Length; i++)
            {
                _brightnessGridLines[i] = new Line { Stroke = gridBrush, StrokeThickness = 0.5, Opacity = 0.5 };
                BrightnessCanvas.Children.Add(_brightnessGridLines[i]);
            }

            var labelBrush = (Brush)FindResource("MutedTextBrush");
            string[] yText = { "255", "128", "0" };
            for (int i = 0; i < _yAxisLabels.Length; i++)
            {
                _yAxisLabels[i] = new TextBlock { Text = yText[i], Foreground = labelBrush, FontSize = 9 };
                BrightnessCanvas.Children.Add(_yAxisLabels[i]);
            }

            _xAxisStartLabel = new TextBlock { Text = "-120s", Foreground = labelBrush, FontSize = 9 };
            _xAxisEndLabel = new TextBlock { Text = "now", Foreground = labelBrush, FontSize = 9 };
            BrightnessCanvas.Children.Add(_xAxisStartLabel);
            BrightnessCanvas.Children.Add(_xAxisEndLabel);
        }

        /// <summary>Repositions the static axis chrome for the canvas's current size (called every redraw).</summary>
        private void RepositionAxisElements(double w, double h)
        {
            for (int i = 0; i < GridLumaLevels.Length; i++)
            {
                double y = h - h * GridLumaLevels[i] / 255.0;
                _brightnessGridLines[i].X1 = 0;
                _brightnessGridLines[i].X2 = w;
                _brightnessGridLines[i].Y1 = y;
                _brightnessGridLines[i].Y2 = y;
            }

            Canvas.SetLeft(_yAxisLabels[0], 2); Canvas.SetTop(_yAxisLabels[0], 0);           // 255, top
            Canvas.SetLeft(_yAxisLabels[1], 2); Canvas.SetTop(_yAxisLabels[1], h / 2 - 6);   // 128, middle
            Canvas.SetLeft(_yAxisLabels[2], 2); Canvas.SetTop(_yAxisLabels[2], h - 24);      // 0, above the x-axis row

            Canvas.SetLeft(_xAxisStartLabel, 2); Canvas.SetTop(_xAxisStartLabel, h - 12);
            Canvas.SetLeft(_xAxisEndLabel, w - 26); Canvas.SetTop(_xAxisEndLabel, h - 12);
        }
    }
}
