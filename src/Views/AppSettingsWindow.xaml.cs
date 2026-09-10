using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using MatroxFrameGrabber.Infrastructure;
using MatroxFrameGrabber.ViewModels;

namespace MatroxFrameGrabber.Views
{
    /// <summary>
    /// The application's settings, in one window.
    ///
    /// Replaces the "⚙ Rec" popup, which was called recording settings and held three rows of
    /// which one was about recording - the ffmpeg path is a read-only diagnostic and the display
    /// rate is about the preview. The output folder was on the toolbar, and the recording backend
    /// had nowhere to go at all.
    ///
    /// Not modal, and only one at a time: unlike a camera window there is nothing to compare it
    /// against, so a second copy of the same settings would only be a way to make them disagree.
    /// </summary>
    public partial class AppSettingsWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public AppSettingsWindow(MainViewModel viewModel, Window owner)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = viewModel;

            if (owner != null)
            {
                Owner = owner;
                Left = Math.Max(0, owner.Left + 60);
                Top = owner.Top + 60;
            }
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
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

        private void BrowseSegments_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null) return;
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select the folder for rolling anomaly segments (local disk)";
                dialog.UseDescriptionForTitle = true;
                if (Directory.Exists(_viewModel.Output.SegmentFolder))
                    dialog.SelectedPath = _viewModel.Output.SegmentFolder;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    _viewModel.Output.SegmentFolder = dialog.SelectedPath;
            }
        }

        private void OpenOutput_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string folder = _viewModel?.Output.EnsureFolder();
                if (!string.IsNullOrEmpty(folder))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MilErrorLog.Note($"settings: could not open the output folder - {ex.Message}");
            }
        }
    }
}
