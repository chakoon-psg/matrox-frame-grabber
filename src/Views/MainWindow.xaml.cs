using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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
                }

                _viewModel = new MainViewModel(_manager);
                DataContext = _viewModel;
            }
            catch (Exception ex)
            {
                _initError = ex.Message;
            }

            InitializeComponent();
        }

        private void OnRecordingFailed(CameraChannel channel, string error)
        {
            MessageBox.Show($"Recording stopped: {error}", channel.Name,
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
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
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

        private void RecordAll_Click(object sender, RoutedEventArgs e)
        {
            _viewModel?.ToggleRecordAll();
        }

        // ----- Fullscreen -----

        private void Pane_FullscreenRequested(object sender, CameraChannel channel)
        {
            EnterFullscreen(channel);
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
    }
}
