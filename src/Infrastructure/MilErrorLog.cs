using System;
using System.IO;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Appends MIL failures to a log file. This exists because MIL's own error reporting is a modal
    /// dialog raised on whichever thread failed — on an acquisition thread that stalls the channel,
    /// and behind the main window it reads as a frozen camera. Printing is therefore disabled
    /// process-wide (MilApplicationManager.Allocate) and errors land here instead.
    ///
    /// Deliberately best-effort: a logging failure must never propagate into an acquisition hook.
    /// </summary>
    public static class MilErrorLog
    {
        private static readonly object Gate = new object();

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MatroxFrameGrabber", "mil-errors.log");

        /// <summary>Trim the file once it passes this, so a repeating fault cannot fill the disk.</summary>
        private const long MaxBytes = 2 * 1024 * 1024;

        /// <summary>
        /// Records one failure. <paramref name="context"/> says what the app was attempting, in
        /// words a field engineer can act on — "Camera 1: write DecimationHorizontal", not "SetInt".
        /// </summary>
        public static void Write(string context, Exception e)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                        File.WriteAllText(LogPath, $"--- trimmed {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}");

                    string message = e == null ? "(no exception)" : e.Message.Replace('\r', ' ').Replace('\n', ' ');
                    File.AppendAllText(LogPath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {context}  |  {message}{Environment.NewLine}");
                }
            }
            catch (Exception)
            {
                // A failure to log must never reach the caller — this is called from acquisition
                // hooks and from catch blocks that exist to keep the app running.
            }
        }
    }
}
