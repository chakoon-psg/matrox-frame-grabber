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
        private CameraPaneView _fullscreenPane;
        private RoiEditSurface _fullscreenRoi;
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
                ((MainViewModel)DataContext).StatsRefreshed += OnStatsRefreshed;
        }

        /// <summary>
        /// Runs on every 500 ms stats tick: redraws the brightness strip and repositions each
        /// pane's analysis-ROI rectangle. The ROI rectangle piggybacks on this tick rather than
        /// getting a timer of its own, because MIL's zoom/pan is native and raises no event we
        /// could hook — polling here is the only way to follow the operator's view changes.
        /// </summary>
        private void OnStatsRefreshed()
        {
            RedrawBrightness();
            Pane0?.RefreshRoiOverlay();
            Pane1?.RefreshRoiOverlay();
            Pane2?.RefreshRoiOverlay();
            Pane3?.RefreshRoiOverlay();
            // The overlay follows the same tick: it shows the same rectangle over the same
            // display, and MIL raises no event when the operator zooms.
            _fullscreenRoi?.Refresh();
        }

        /// <summary>
        /// Reports something the operator should see. In an unattended --autostart run there is
        /// nobody to dismiss a dialog, and a modal one would hold the process until the caller
        /// gives up waiting — so the message goes to the log instead. Interactive runs are
        /// unchanged.
        /// </summary>
        private static void Report(string message, string title, MessageBoxImage icon)
        {
            if (App.Unattended)
            {
                MilErrorLog.Note($"autostart: {title} - {(message ?? "").Replace('\n', ' ')}");
                return;
            }
            MessageBox.Show(message, title, MessageBoxButton.OK, icon);
        }

        private void OnRecordingFailed(CameraChannel channel, string error)
        {
            Report($"Recording stopped: {error}", channel.Name, MessageBoxImage.Warning);
        }

        private void OnGrabFailed(CameraChannel channel, string error)
        {
            Report($"Could not start acquisition: {error}", channel.Name, MessageBoxImage.Warning);
        }

        private void OnCameraLost(CameraChannel channel)
        {
            Report("Camera was disconnected. Press Stop, reconnect it, then Start again.",
                channel.Name, MessageBoxImage.Warning);
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            NativeMethods.UseImmersiveDarkTitleBar(this);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initError != null)
            {
                Report(_initError, "MIL Allocation Error", MessageBoxImage.Error);
                if (App.Unattended)
                    Close();   // nothing to run, and nobody to close the window
                return;
            }

            if (App.Unattended)
                BeginAutoRun(App.AutoRunSeconds);
        }

        /// <summary>
        /// Unattended run: grab on every present camera for a fixed number of seconds, then stop
        /// and close. Each channel's <c>grab stopped</c> line in the log is the result.
        ///
        /// Closing through <see cref="Window.Close"/> rather than ending the process is the whole
        /// point of doing it this way: Window_Closing is what frees MIL and restores the board's
        /// Bayer conversion, and a killed app is what leaves the camera's geometry nodes silently
        /// refusing writes afterwards (see CLAUDE.md).
        /// </summary>
        private void BeginAutoRun(int seconds)
        {
            if (_viewModel == null)
                return;

            MilErrorLog.Note($"autostart: grabbing for {seconds}s, then closing");
            _viewModel.StartAllCommand.Execute(null);

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(seconds)
            };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                // Straight to the view model rather than StopAll_Click: that one asks the operator
                // to confirm while a recording is in progress, and there is nobody to answer.
                _viewModel.StopAllCommand.Execute(null);
                Close();
            };
            timer.Start();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_viewModel != null)
                _viewModel.StatsRefreshed -= OnStatsRefreshed;
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

        private void Pane_ApplyToAllRequested(object sender, EventArgs e)
        {
            if ((sender as CameraPaneView)?.DataContext is CameraChannel source)
                _viewModel?.ApplyAllSettings(source);
        }

        private void Pane_FullscreenRequested(object sender, CameraChannel channel)
        {
            EnterFullscreen(sender as CameraPaneView, channel);
        }

        /// <summary>
        /// Moves the pane's display control into the overlay. Creating a second control for the
        /// same DisplayId is what left the pane scaled to the overlay after returning — MIL has one
        /// zoom per display, and "fit to window" cannot choose between two windows.
        /// </summary>
        private void EnterFullscreen(CameraPaneView pane, CameraChannel channel)
        {
            if (pane == null || channel == null || !channel.CameraPresent || _fullscreenChannel != null)
                return;

            MILWPFDisplay display = pane.DetachDisplay();
            if (display == null)
                return;

            _fullscreenChannel = channel;
            _fullscreenPane = pane;
            _fullscreenDisplay = display;
            FullscreenTitle.Text = $"{channel.Name} — double-click or press ESC to exit";
            FullscreenContentGrid.Children.Insert(0, display);

            // Edit mode starts off every time. Carrying the pane toggle's state across would leave
            // the operator wondering why the handles are showing on a screen they just opened.
            FullscreenRoiToggle.IsChecked = false;
            _fullscreenRoi = new RoiEditSurface(FullscreenBorder, FullscreenContentGrid,
                                                () => _fullscreenChannel, "fullscreen");

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
            _fullscreenRoi?.ExitEditMode();
            _fullscreenRoi = null;

            if (_fullscreenDisplay != null)
                FullscreenContentGrid.Children.Remove(_fullscreenDisplay);
            _fullscreenDisplay = null;

            MainContent.Visibility = Visibility.Visible;

            CameraPaneView pane = _fullscreenPane;
            CameraChannel channel = _fullscreenChannel;
            _fullscreenPane = null;
            _fullscreenChannel = null;

            // Synchronously: the control needs its parent back before the layout pass, or the fit
            // below is computed against a pane that has no size yet.
            pane?.ReattachDisplay();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                channel.FitToWindow();
                // The zoom the pane will map its ROI rectangle with. Recorded once per exit,
                // because the whole failure was this number staying at the overlay's fit.
                if (channel.TryGetViewGeometry(out int fw, out int fh, out double zoom, out double ox, out double oy))
                    MilErrorLog.Note($"{channel.Name}: back from fullscreen - frame {fw}x{fh}, "
                                   + $"zoom {zoom:F3}, offset {ox:F0},{oy:F0}");
                else
                    MilErrorLog.Note($"{channel.Name}: back from fullscreen - view geometry unreadable");
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ExitFullscreen_Click(object sender, RoutedEventArgs e) => ExitFullscreen();

        private void FullscreenBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (FullscreenOverlay.Visibility == Visibility.Visible)
                _fullscreenChannel?.FitToWindow();
        }

        private void FullscreenBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Edit mode takes the press first, so a drag near the rectangle cannot be read as the
            // double-click that leaves fullscreen.
            if (_fullscreenRoi != null && _fullscreenRoi.TryBeginDrag(e))
            {
                e.Handled = true;
                return;
            }

            if (e.ClickCount == 2)
            {
                ExitFullscreen();
                e.Handled = true;
            }
        }

        private void FullscreenBorder_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_fullscreenRoi == null) return;
            bool wasDragging = _fullscreenRoi.IsDragging;
            _fullscreenRoi.ContinueDrag(e);
            if (wasDragging)
                e.Handled = true;
        }

        private void FullscreenBorder_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_fullscreenRoi == null || !_fullscreenRoi.IsDragging) return;
            _fullscreenRoi.EndDrag(e);
            e.Handled = true;
        }

        private void FullscreenRoiToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_fullscreenRoi == null) return;
            if (FullscreenRoiToggle.IsChecked == true)
            {
                _fullscreenRoi.EditMode = true;
                _fullscreenRoi.Refresh();
            }
            else
            {
                _fullscreenRoi.ExitEditMode();
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
                // Mid-drag, ESC belongs to the drag: abandoning the rectangle you are dragging is
                // what the key means there, and leaving fullscreen as well would take away the
                // screen you were working on.
                if (_fullscreenRoi != null && _fullscreenRoi.IsDragging)
                    _fullscreenRoi.CancelDrag();
                else
                    ExitFullscreen();   // also removes this hook
                handled = true;         // swallow ESC so MIL doesn't act on it
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
        // The gridlines belong to the plot canvas; the labels belong to the gutter beside it.
        private static readonly double[] GridLumaLevels = { 64, 128, 192 };
        private readonly Line[] _brightnessGridLines = new Line[GridLumaLevels.Length];
        private readonly TextBlock[] _yAxisLabels = new TextBlock[3];   // "255" / "128" / "0"

        /// <summary>
        /// Redraws the brightness strip. The vertical axis is pinned to 0-255 rather than scaled to
        /// the data: an auto-scaled axis hides the slow drift the graph exists to reveal.
        /// </summary>
        private void RedrawBrightness()
        {
            var vm = DataContext as MainViewModel;
            // Cleared before the early-return guard: a strip whose canvas hasn't been laid out yet
            // must never keep showing labels from a previous state.
            BrightnessLegend.Children.Clear();
            if (vm == null || BrightnessCanvas.ActualWidth <= 0)
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

                BrightnessLegend.Children.Add(BuildLegendEntry(channel, i));
            }

            // Diagnostic tooltip on the strip itself (it used to hang off the Brightness toggle,
            // which no longer exists): makes the 500 ms budget check (spec verification 3) and a
            // permanently-failing channel (F6) both readable at a glance, instead of requiring a
            // debugger.
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
            BrightnessStrip.ToolTip = diag.ToString();
        }

        /// <summary>
        /// Builds one legend entry: a colour swatch followed by neutral-coloured readings.
        ///
        /// The swatch reuses the polyline's own <see cref="Brush"/> instance, so the legend colour
        /// cannot drift from the line it labels. It exists because colouring the *text* was not
        /// enough to tell four pastel 1.5 px lines apart, and because the clipping warning used to
        /// repaint the whole entry red — losing the channel's identity at exactly the moment the
        /// reader needs to know which channel is clipping. Only the clip figure carries the
        /// warning colour now.
        /// </summary>
        private FrameworkElement BuildLegendEntry(CameraChannel channel, int index)
        {
            BrightnessSample latest = channel.Brightness.Latest;
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };

            entry.Children.Add(new Rectangle
            {
                Width = 16,
                Height = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = _brightnessLines[index].Stroke,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });

            var textBrush = (Brush)FindResource("TextBrush");
            var mutedBrush = (Brush)FindResource("MutedTextBrush");

            entry.Children.Add(new TextBlock
            {
                Text = $"{channel.Name}  {latest.Luma:F0}",
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            entry.Children.Add(new TextBlock
            {
                Text = $"clip {latest.ClippedPct:F1}%",
                Foreground = latest.ClippedPct >= ClipWarnPercent ? (Brush)FindResource("WarnBrush") : mutedBrush,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            entry.Children.Add(new TextBlock
            {
                Text = $"blk {latest.BlackPct:F1}%",
                Foreground = mutedBrush,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            return entry;
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

            // Labels go in the gutter canvas, not the plot canvas. Both live in the same Grid row,
            // so they share a height and the y coordinates computed below line up with the gridlines.
            var labelBrush = (Brush)FindResource("MutedTextBrush");
            string[] yText = { "255", "128", "0" };
            for (int i = 0; i < _yAxisLabels.Length; i++)
            {
                _yAxisLabels[i] = new TextBlock { Text = yText[i], Foreground = labelBrush, FontSize = 9 };
                BrightnessAxisGutter.Children.Add(_yAxisLabels[i]);
            }
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

            // Right-aligned in the gutter so the numbers sit against the plot edge they annotate.
            // "0" sits a full line-height up from the bottom so its baseline reads as the 0 line
            // rather than hanging below it.
            Canvas.SetRight(_yAxisLabels[0], 4); Canvas.SetTop(_yAxisLabels[0], 0);           // 255, top
            Canvas.SetRight(_yAxisLabels[1], 4); Canvas.SetTop(_yAxisLabels[1], h / 2 - 6);   // 128, middle
            Canvas.SetRight(_yAxisLabels[2], 4); Canvas.SetTop(_yAxisLabels[2], h - 12);      // 0, bottom
        }
    }
}
