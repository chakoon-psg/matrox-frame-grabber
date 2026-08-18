using System.Windows;
using System.Windows.Threading;

namespace MatroxFrameGrabber
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Last-resort backstop: a MIL call that throws on the UI thread would otherwise terminate
        /// the process mid-capture. Individual call sites still handle their own failures — this
        /// only keeps an unforeseen one from killing an in-progress recording. It deliberately does
        /// NOT swallow exceptions off the UI thread, which stay fatal.
        /// </summary>
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(e.Exception.Message, "Unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
