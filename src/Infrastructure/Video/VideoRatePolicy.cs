using System;
using System.Globalization;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// The arithmetic that decides what frame rate a recording claims and which frames reach it.
    ///
    /// This is here rather than beside the encoder because both recording defects found on
    /// 2026-09-10 were arithmetic, and arithmetic is testable without MIL or ffmpeg. A 120.0 s run
    /// at 124.316 fps produced a file that read as 81.07 s at 184.06 fps, with every frame present:
    /// the rate was taken from what the camera had been asked for rather than what it can deliver,
    /// and the output rate was left for ffmpeg to guess. Both are one function each now.
    /// </summary>
    public static class VideoRatePolicy
    {
        /// <summary>Below this a rate is treated as absent rather than slow.</summary>
        public const double MinUsableFps = 1.0;

        /// <summary>Last resort when nothing else answered. Plays wrong, but plays.</summary>
        public const double FallbackFps = 30.0;

        /// <summary>
        /// The rate to write into a file's header, in order of trust.
        ///
        /// <paramref name="resultingFps"/> is the camera's own ResultingFrameRate: what it says it
        /// can deliver at the current exposure and decimation. It is available before the first
        /// frame, which is what makes it usable here - recording starts within a couple of hundred
        /// milliseconds of the grab, and the measured rate does not exist yet.
        ///
        /// <paramref name="measuredFps"/> is M_PROCESS_FRAME_RATE, true but late: it is filled on
        /// the 500 ms stats tick and, in this app, never cleared between runs, so at recording
        /// start it is either zero or the previous run's - possibly at a different exposure.
        ///
        /// <paramref name="nominalFps"/> is M_SELECTED_FRAME_RATE, the configured
        /// AcquisitionFrameRate. This is the one that caused the defect: it answered 184 for a
        /// camera the 8000 us exposure limits to 124.316, and 184/124.316 is the 1.48x the file
        /// played at. It stays only as the last thing to try.
        /// </summary>
        public static double Declared(double resultingFps, double measuredFps, double nominalFps)
        {
            if (Usable(resultingFps)) return resultingFps;
            if (Usable(measuredFps)) return measuredFps;
            if (Usable(nominalFps)) return nominalFps;
            return FallbackFps;
        }

        /// <summary>Whether a rate is a real answer rather than zero, negative, or NaN.</summary>
        public static bool Usable(double fps) =>
            !double.IsNaN(fps) && !double.IsInfinity(fps) && fps >= MinUsableFps;

        /// <summary>
        /// The rate of a file that receives only every <paramref name="everyNthFrame"/> frame.
        ///
        /// The long session recording is meant to be "30 fps", and declaring 30 would be wrong:
        /// taking every fourth frame of 124.316 fps gives 31.079, and a header saying 30 makes the
        /// file 3.6% slow. The requested rate is a divisor, never the declared value.
        /// </summary>
        public static double FileFps(double declaredFps, int everyNthFrame)
        {
            if (!Usable(declaredFps)) throw new ArgumentOutOfRangeException(nameof(declaredFps));
            if (everyNthFrame < 1) throw new ArgumentOutOfRangeException(nameof(everyNthFrame));
            return declaredFps / everyNthFrame;
        }

        /// <summary>
        /// The divisor that gets closest to <paramref name="wantedFps"/> without exceeding the
        /// source. Never returns 0, so a wanted rate above the source simply takes every frame.
        /// </summary>
        public static int EveryNthFor(double sourceFps, double wantedFps)
        {
            if (!Usable(sourceFps)) throw new ArgumentOutOfRangeException(nameof(sourceFps));
            if (!Usable(wantedFps) || wantedFps >= sourceFps) return 1;
            return Math.Max(1, (int)Math.Round(sourceFps / wantedFps));
        }

        /// <summary>Whether a frame belongs to an output that takes every Nth frame.</summary>
        public static bool ShouldFeed(long frameNumber, int everyNthFrame) =>
            everyNthFrame <= 1 || frameNumber % everyNthFrame == 0;

        /// <summary>
        /// Frames between keyframes for a wanted interval in seconds, at least one.
        ///
        /// x264's default is 250 frames, which at 124.316 fps is 2.01 s. Segment boundaries land on
        /// keyframes, so the interval bounds how closely a segment can follow its nominal length.
        /// </summary>
        public static int KeyframeInterval(double fileFps, double seconds)
        {
            if (!Usable(fileFps)) throw new ArgumentOutOfRangeException(nameof(fileFps));
            if (seconds <= 0.0) return 0;
            return Math.Max(1, (int)Math.Round(fileFps * seconds));
        }

        /// <summary>
        /// A rate as ffmpeg should read it: invariant culture, three decimals.
        ///
        /// Three decimals because 124.316 has to survive - ffmpeg parses it to 31079/250 exactly,
        /// and a comma from a Korean or German locale would make it parse as something else
        /// entirely.
        /// </summary>
        public static string Format(double fps) =>
            fps.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
