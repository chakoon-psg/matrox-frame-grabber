using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Writes one CSV row per channel per stats tick for the duration of a grab, so that a run
    /// becomes a dataset instead of a number that scrolled off the screen.
    ///
    /// This exists because the detector's thresholds cannot be chosen from first principles. The
    /// depth threshold has to sit above the false-positive floor, and that floor is whatever the
    /// luma of a healthy panel happens to wander by over minutes — an optical and electrical
    /// property of the rig, not something the code can know. The brightness strip shows the
    /// instantaneous value and BrightnessHistory holds two minutes; neither survives the run.
    ///
    /// MIL-free on purpose: the caller passes plain numbers, so this is unit-testable and the
    /// tests can source-include it like the rest of Infrastructure.
    /// </summary>
    public sealed class BrightnessLog : IDisposable
    {
        /// <summary>
        /// Row cap. At the 500 ms tick with three channels this is six rows a second, so the cap
        /// is about nine hours. A run left going overnight stops writing rather than filling the
        /// disk; the rows already written stay valid.
        /// </summary>
        public const int MaxRows = 200_000;

        public const string Header =
            "iso_time,elapsed_s,channel,decim,exposure_us,roi_w,roi_h,frames,fps,missed,"
          + "luma,clip_pct,black_pct,anomalies,reduce_us,grids,depth,coherence,baseline";

        /// <summary>
        /// Where runs are written: beside the MIL error log, under a folder of its own so a
        /// months-long pile of runs does not bury the two files a person actually opens by hand.
        /// </summary>
        public static string DefaultFolder => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MatroxFrameGrabber", "measurements");

        private StreamWriter _writer;
        private DateTime _startedAt;

        /// <summary>Full path of the file being written, or null when not started.</summary>
        public string Path { get; private set; }

        public bool IsActive => _writer != null;

        /// <summary>Rows written so far, excluding the header.</summary>
        public int Rows { get; private set; }

        /// <summary>
        /// Opens a new file named for the moment the grab started. One file per run: merging runs
        /// into a single file would let a lens or lighting change halfway through look like drift.
        /// Returns false and stays inactive when the file cannot be created — a failed log must not
        /// take the grab down with it.
        /// </summary>
        public bool Start(string folder, DateTime startedAt)
        {
            if (_writer != null) return true;

            try
            {
                Directory.CreateDirectory(folder);
                string path = System.IO.Path.Combine(
                    folder, "brightness-" + startedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");

                var writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
                writer.WriteLine(Header);

                _writer = writer;
                _startedAt = startedAt;
                Path = path;
                Rows = 0;
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                MilErrorLog.Write("brightness log: could not create the file", e);
                _writer = null;
                Path = null;
                return false;
            }
        }

        /// <summary>
        /// Appends one reading. <paramref name="hasBrightness"/> is false when the meter had
        /// nothing to read; the row is still written, with empty luma columns, because the gap
        /// itself is evidence — a channel whose meter keeps failing would otherwise look like a
        /// channel that was simply idle.
        /// </summary>
        public void Append(
            DateTime now, string channel, int decimation, double exposureUs, int roiWidth, int roiHeight,
            long frames, double fps, long missed,
            bool hasBrightness, float luma, float clippedPct, float blackPct,
            long anomalies, double reduceUs,
            long grids, double depth, double coherence, double baseline)
        {
            if (_writer == null || Rows >= MaxRows) return;

            var inv = CultureInfo.InvariantCulture;
            string luminance = hasBrightness
                ? string.Format(inv, "{0:F2},{1:F3},{2:F3}", luma, clippedPct, blackPct)
                : ",,";

            try
            {
                _writer.WriteLine(string.Format(
                    inv, "{0},{1:F1},{2},{3},{4:F0},{5},{6},{7},{8:F2},{9},{10},{11},{12:F1},"
                       + "{13},{14:F4},{15:F4},{16:F2}",
                    now.ToString("yyyy-MM-dd HH:mm:ss.fff", inv),
                    (now - _startedAt).TotalSeconds,
                    Csv(channel), decimation, exposureUs, roiWidth, roiHeight,
                    frames, fps, missed, luminance, anomalies, reduceUs,
                    grids, depth, coherence, baseline));
                Rows++;
            }
            catch (IOException e)
            {
                MilErrorLog.Write("brightness log: write failed, stopping the log", e);
                Stop();
            }
        }

        /// <summary>Closes the file. Safe to call when not started, and safe to call twice.</summary>
        public void Stop()
        {
            if (_writer == null) return;

            try { _writer.Dispose(); }
            catch (IOException) { /* the rows already flushed are what matter */ }
            _writer = null;
        }

        public void Dispose() => Stop();

        /// <summary>Quotes a field only when it needs it, so the common case stays readable.</summary>
        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
