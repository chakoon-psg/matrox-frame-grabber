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
    /// Deliberately best-effort: a logging failure must never propagate to the caller.
    ///
    /// DO NOT call this per frame. It is a synchronous disk write under a process-global lock, and
    /// at 184 fps across three channels an error storm would serialise every acquisition thread
    /// behind file I/O — which is the same stall this logging exists to replace. Acquisition-path
    /// code should count failures and let the 500 ms stats tick report them.
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
                    {
                        // Keep the tail, not nothing: the oldest lines say when a fault storm
                        // started, which is usually the part worth reading.
                        string[] all = File.ReadAllLines(LogPath);
                        int keep = all.Length / 2;
                        File.WriteAllLines(LogPath, all.AsSpan(all.Length - keep).ToArray());
                    }

                    string message = e == null ? "(no exception)" : e.Message.Replace('\r', ' ').Replace('\n', ' ');
                    File.AppendAllText(LogPath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {context}  |  {message}{Environment.NewLine}");
                }
            }
            catch (Exception)
            {
                // A failure to log must never reach the caller — this is called from catch blocks
                // that exist to keep the app running.
            }
        }
    }
}
