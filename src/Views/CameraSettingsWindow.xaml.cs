using System;
using System.Windows;
using MatroxFrameGrabber.Mil;

namespace MatroxFrameGrabber.Views
{
    /// <summary>
    /// One camera's settings in a window of its own, for when a pane is too narrow for them.
    ///
    /// Not modal: commissioning means changing an exposure while watching what it does to the
    /// image, and a modal window blocks the pane it is being judged against. Several can be open
    /// at once, which is also how the channels get compared — three windows side by side, rather
    /// than a table on the main window taking permanent space for values that are set once.
    /// </summary>
    public partial class CameraSettingsWindow : Window
    {
        public CameraSettingsWindow(CameraChannel channel, Window owner)
        {
            InitializeComponent();

            DataContext = channel;
            Title = channel != null ? $"{channel.Name} — Settings" : "Settings";

            // Owned, so it closes with the main window and stays in front of it. Placed beside the
            // owner rather than centred on it: the point of a second window is seeing both.
            if (owner != null)
            {
                Owner = owner;
                Left = Math.Max(0, owner.Left + owner.Width - Width - 40);
                Top = owner.Top + 60;
            }

            Settings.ApplyToAllRequested += (s, e) => ApplyToAllRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Forwarded from the hosted control so the pane can re-raise it as its own.</summary>
        public event EventHandler ApplyToAllRequested;
    }
}
