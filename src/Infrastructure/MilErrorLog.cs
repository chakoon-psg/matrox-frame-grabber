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

        /// <summary>
        /// Added to the log's file name, so processes sharing one board do not share one file.
        ///
        /// The lock above is process-local: it orders writes within a process and does nothing
        /// between them. Two processes appending to the same file interleave and lose lines — which
        /// is not a theoretical worry, it silently corrupted the first split-process measurement
        /// taken here, making a channel that had run fine look like it never started.
        ///
        /// Set this before the first write. A cross-process mutex would be the other answer, but a
        /// separate file per process is simpler and gives per-camera logs, which is what anyone
        /// running a split would want to read anyway.
        /// </summary>
        public static string FileSuffix
        {
            get => _fileSuffix;
            set
            {
                lock (Gate)
                {
                    _fileSuffix = string.IsNullOrWhiteSpace(value) ? "" : value;
                    _logPath = null;
                }
            }
        }

        private static string _fileSuffix = "";
        private static string _logPath;

        private static string LogPath
        {
            get
            {
                if (_logPath == null)
                    _logPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "MatroxFrameGrabber", $"mil-errors{_fileSuffix}.log");
                return _logPath;
            }
        }

        /// <summary>Trim the file once it passes this, so a repeating fault cannot fill the disk.</summary>
        private const long MaxBytes = 2 * 1024 * 1024;

        /// <summary>
        /// Records one failure. <paramref name="context"/> says what the app was attempting, in
        /// words a field engineer can act on — "Camera 1: write DecimationHorizontal", not "SetInt".
        /// </summary>
        public static void Write(string context, Exception e) => Append(context, e);

        /// <summary>
        /// Records a diagnostic line that is not a failure — a hardware state worth having in the
        /// log when someone asks why the camera refused something. Same file, same trimming, and
        /// the same best-effort contract and per-frame prohibition as <see cref="Write"/>.
        /// </summary>
        public static void Note(string context) => Append(context, null);

        private static void Append(string context, Exception e)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                    {
                        // Keep the newest half rather than wiping the file: the old behaviour threw
                        // away all history at the trim boundary.
                        string[] all = File.ReadAllLines(LogPath);
                        int keep = all.Length / 2;
                        File.WriteAllLines(LogPath, all.AsSpan(all.Length - keep).ToArray());
                        File.AppendAllText(LogPath,
                            $"--- trimmed {DateTime.Now:yyyy-MM-dd HH:mm:ss}, older entries dropped ---{Environment.NewLine}");
                    }

                    // A Note has no exception; keep its line clean rather than tacking on a
                    // placeholder that reads like a swallowed error.
                    string message = e?.Message.Replace('\r', ' ').Replace('\n', ' ');
                    string suffix = message == null ? "" : "  |  " + message;
                    File.AppendAllText(LogPath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {context}{suffix}{Environment.NewLine}");
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
