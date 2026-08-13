using System;
using System.Windows;
using System.Windows.Input;
using Matrox.MatroxImagingLibrary;
using Matrox.MatroxImagingLibrary.WPF;
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
        private MilApplicationManager _manager;
        private MainViewModel _viewModel;
        private CameraChannel _fullscreenChannel;
        private MILWPFDisplay _fullscreenDisplay;
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
                _viewModel = new MainViewModel(_manager);
                DataContext = _viewModel;
            }
            catch (Exception ex)
            {
                _initError = ex.Message;
            }

            InitializeComponent();
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
            _viewModel?.Shutdown();
            DataContext = null;
            _manager?.Free();
            _manager = null;
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

            Dispatcher.BeginInvoke(new Action(() => _fullscreenChannel?.FitToWindow()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ExitFullscreen()
        {
            if (_fullscreenChannel == null)
                return;

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

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && FullscreenOverlay.Visibility == Visibility.Visible)
            {
                ExitFullscreen();
                e.Handled = true;
            }
        }
    }
}
