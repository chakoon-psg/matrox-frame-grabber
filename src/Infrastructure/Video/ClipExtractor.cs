using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>Cuts a window out of already-encoded files.</summary>
    public interface IClipExtractor
    {
        /// <summary>What this is, for the log.</summary>
        string Name { get; }

        /// <summary>
        /// Writes the covered window to <paramref name="outPath"/>. Returns false with a reason
        /// rather than throwing: a clip that cannot be cut must not take the run with it.
        /// </summary>
        bool Extract(SegmentCoverage coverage, string outPath, out string error);
    }

    /// <summary>
    /// Cuts a clip with ffmpeg, copying the encoded stream rather than re-encoding it.
    ///
    /// Deliberately separate from <c>IVideoSink</c>. ffmpeg can do this with a stream copy and
    /// whether MIL can is unknown, so encoding and cutting have to be swappable on their own - a
    /// licence that brings MIL encoding may still leave the cutting here.
    ///
    /// Measured 2026-09-10: concatenating six 2 s segments and taking ten seconds landed within one
    /// frame of the requested window (1245 frames against 1243, and the brightness code stamped in
    /// the frames read back one frame from where it was asked for). ffmpeg reports "could not seek"
    /// on the concat input and reads from the first file's start instead, which is why the ring must
    /// not delete a file a pending clip still needs - and also why the cut is accurate rather than
    /// rounded to a keyframe.
    /// </summary>
    public sealed class FfmpegClipExtractor : IClipExtractor
    {
        private readonly string _ffmpegPath;

        /// <summary>How long to wait for the cut. A ten-second stream copy takes well under a second.</summary>
        public int TimeoutMs { get; set; } = 30000;

        public FfmpegClipExtractor(string ffmpegPath) { _ffmpegPath = ffmpegPath; }

        public string Name => "ffmpeg -c copy";

        public bool Extract(SegmentCoverage coverage, string outPath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(_ffmpegPath)) { error = "ffmpeg was not found."; return false; }
            if (!coverage.Any) { error = "no segments cover that window."; return false; }
            if (coverage.LengthSec <= 0.0) { error = "the window has no length."; return false; }

            string listPath = Path.Combine(Path.GetDirectoryName(outPath) ?? ".",
                                           Path.GetFileNameWithoutExtension(outPath) + ".concat.txt");
            try
            {
                // The concat demuxer's own list format. Single quotes doubled, which is how it
                // escapes them.
                var sb = new StringBuilder();
                foreach (SegmentEntry e in coverage.Files)
                    sb.Append("file '").Append(e.Path.Replace("'", "''")).Append('\'').Append('\n');
                File.WriteAllText(listPath, sb.ToString());

                string args =
                    "-hide_banner -loglevel warning -f concat -safe 0 " +
                    $"-ss {Sec(coverage.OffsetSec)} -i \"{listPath}\" " +
                    $"-t {Sec(coverage.LengthSec)} -c copy -movflags +faststart -y \"{outPath}\"";

                MilErrorLog.Note($"clip: {args}");

                var psi = new ProcessStartInfo(_ffmpegPath, args)
                {
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) { error = "ffmpeg did not start."; return false; }
                    string stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(TimeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        error = "the cut timed out.";
                        return false;
                    }
                    if (p.ExitCode != 0)
                    {
                        error = string.IsNullOrWhiteSpace(stderr)
                            ? $"ffmpeg exited {p.ExitCode}."
                            : stderr.Trim();
                        return false;
                    }
                    // "could not seek" is expected on a concat input and is not a failure: ffmpeg
                    // reads from the first file's start and discards, which is what makes the cut
                    // frame-accurate rather than keyframe-rounded.
                }

                if (!File.Exists(outPath)) { error = "ffmpeg reported success but wrote nothing."; return false; }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(listPath)) File.Delete(listPath); } catch { }
            }
        }

        private static string Sec(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
