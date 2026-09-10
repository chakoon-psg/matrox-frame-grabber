using Matrox.MatroxImagingLibrary;
using MatroxFrameGrabber.Infrastructure;

namespace MatroxFrameGrabber.Mil.Video
{
    /// <summary>Which backend to record through, when more than one could work.</summary>
    public enum VideoSinkPreference
    {
        /// <summary>MIL if it can encode here, otherwise ffmpeg. The default.</summary>
        Auto = 0,
        /// <summary>Always the external ffmpeg process.</summary>
        Ffmpeg = 1,
        /// <summary>MIL only; recording is unavailable if MIL cannot encode.</summary>
        Mil = 2,
    }

    /// <summary>
    /// Chooses a sink and says which and why.
    ///
    /// Recording used to be available exactly when ffmpeg was found on disk, and unavailable
    /// silently otherwise - a greyed-out button with nothing to read. Now the reason is a string
    /// the tooltip and the log can both carry, and adding a backend does not touch the channel.
    ///
    /// If nothing can encode, recording is simply off. Nothing here waits for a licence or retries.
    /// </summary>
    public static class VideoSinkFactory
    {
        /// <summary>
        /// Builds a sink, or returns null with <paramref name="reason"/> set. The caller treats
        /// null as "recording is not available" and shows the reason.
        /// </summary>
        public static IVideoSink Create(MIL_ID sysId, OutputSettings settings,
                                        VideoSinkPreference preference, out string reason)
        {
            bool wantMil = preference == VideoSinkPreference.Mil
                        || (preference == VideoSinkPreference.Auto && MilApplicationManager.MilVideoAvailable);

            if (wantMil)
            {
                // Asked for by preference, or offered because MIL said it can encode. Either way
                // the sink itself decides: it reports MIL's own reason when it cannot start, and
                // Auto then falls through rather than leaving recording off for a soft reason.
                var mil = new MilSeqVideoSink(sysId);
                if (preference == VideoSinkPreference.Mil)
                {
                    reason = mil.Name;
                    return mil;
                }
                mil.Dispose();
            }

            if (preference == VideoSinkPreference.Mil)
            {
                reason = "MIL cannot encode video on this installation.";
                return null;
            }

            string ffmpeg = FfmpegRecorder.ResolveFfmpegPath(settings?.FfmpegPath);
            if (string.IsNullOrEmpty(ffmpeg))
            {
                reason = "ffmpeg was not found. Install it or set the ffmpeg path in settings.";
                return null;
            }

            var sink = new FfmpegVideoSink(sysId, ffmpeg);
            reason = sink.Name;
            return sink;
        }

        /// <summary>
        /// Whether anything can record, and why not when nothing can. Answered without allocating
        /// a sink, for the enabled state of a button at startup.
        /// </summary>
        public static bool CanRecord(OutputSettings settings, VideoSinkPreference preference,
                                     out string reason)
        {
            if (preference != VideoSinkPreference.Ffmpeg && MilApplicationManager.MilVideoAvailable)
            {
                reason = "MIL/Mseq";
                return true;
            }
            if (preference == VideoSinkPreference.Mil)
            {
                reason = "MIL cannot encode video on this installation.";
                return false;
            }

            string ffmpeg = FfmpegRecorder.ResolveFfmpegPath(settings?.FfmpegPath);
            if (string.IsNullOrEmpty(ffmpeg))
            {
                reason = "ffmpeg was not found. Install it or set the ffmpeg path in settings.";
                return false;
            }
            reason = "ffmpeg/libx264";
            return true;
        }
    }
}
