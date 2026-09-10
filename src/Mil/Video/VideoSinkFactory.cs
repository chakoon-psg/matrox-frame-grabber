using Matrox.MatroxImagingLibrary;
using MatroxFrameGrabber.Infrastructure;

namespace MatroxFrameGrabber.Mil.Video
{
    /// <summary>
    /// Builds the sink the preference and the hardware allow, and says which and why.
    ///
    /// Recording used to be available exactly when ffmpeg was found on disk, and unavailable
    /// silently otherwise - a greyed-out button with nothing to read. The decision itself lives in
    /// <see cref="VideoSinkPolicy"/>, which is MIL-free and tested; this class only supplies the
    /// two facts that need MIL to establish and then constructs what was chosen.
    ///
    /// If nothing can encode, recording is simply off. Nothing here waits for a licence or retries.
    /// </summary>
    public static class VideoSinkFactory
    {
        /// <summary>
        /// How far the MIL sink has got, and the reason behind it.
        ///
        /// Two questions, asked separately on purpose. Whether an implementation exists is a
        /// compile-time constant the implementer flips; whether MIL accepts a compression context
        /// is asked of the sink itself, once per process. An implementation delivered for a machine
        /// whose licence refuses compression is <see cref="MilReadiness.Untested"/> - selectable,
        /// because that is the only state it can be tried in, and never chosen automatically.
        /// </summary>
        public static MilReadiness Readiness(out string reason)
        {
            if (!MilSeqVideoSink.Implemented)
            {
                reason = "the MIL sink is not implemented yet";
                return MilReadiness.NotImplemented;
            }
            if (MilApplicationManager.MilContextAvailable)
            {
                reason = "ok";
                return MilReadiness.Ready;
            }
            reason = MilApplicationManager.MilContextReason;
            return MilReadiness.Untested;
        }

        /// <summary>
        /// Builds a sink, or returns null with <paramref name="reason"/> set. The caller treats
        /// null as "recording is not available" and shows the reason.
        /// </summary>
        public static IVideoSink Create(MIL_ID sysId, OutputSettings settings,
                                        VideoSinkPreference preference, out string reason)
        {
            MilReadiness mil = Readiness(out _);
            string ffmpeg = FfmpegRecorder.ResolveFfmpegPath(settings?.FfmpegPath);

            switch (VideoSinkPolicy.Choose(preference, mil, !string.IsNullOrEmpty(ffmpeg), out reason))
            {
                case SinkChoice.Mil: return new MilSeqVideoSink(sysId);
                case SinkChoice.Ffmpeg: return new FfmpegVideoSink(sysId, ffmpeg);
                default: return null;
            }
        }

        /// <summary>
        /// Whether anything can record, and why not when nothing can. Answered without allocating
        /// a sink, for the enabled state of a button at startup.
        /// </summary>
        public static bool CanRecord(OutputSettings settings, VideoSinkPreference preference,
                                     out string reason)
        {
            MilReadiness mil = Readiness(out _);
            string ffmpeg = FfmpegRecorder.ResolveFfmpegPath(settings?.FfmpegPath);
            return VideoSinkPolicy.Choose(preference, mil, !string.IsNullOrEmpty(ffmpeg), out reason)
                   != SinkChoice.None;
        }

        /// <summary>
        /// Whether the MIL option can be offered at all, and what to say about it. For the settings
        /// window: a radio button that cannot be picked has to explain itself.
        /// </summary>
        public static bool MilSelectable(out string reason)
        {
            MilReadiness mil = Readiness(out reason);
            return VideoSinkPolicy.MilSelectable(mil);
        }
    }
}
