namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>Which backend to record through, when more than one could work. Persisted.</summary>
    public enum VideoSinkPreference
    {
        /// <summary>MIL when it is known to work here, otherwise ffmpeg. The default.</summary>
        Auto = 0,
        /// <summary>Always the external ffmpeg process.</summary>
        Ffmpeg = 1,
        /// <summary>MIL only. Recording is unavailable if MIL cannot start - no silent fallback.</summary>
        Mil = 2,
    }

    /// <summary>
    /// How far the MIL sink has got. Two independent facts, deliberately not collapsed into one:
    /// whether the code exists, and whether this installation's MIL accepts a compression context.
    ///
    /// Collapsing them is what made the delivered code untestable. The startup probe asks MseqAlloc
    /// and, on a machine whose licence refuses it, would disable the option - so an implementation
    /// handed back by the board's supplier could never be run here at all. And the probe is a proxy
    /// in the first place: it asks for one output format, and an implementation that uses another
    /// may work where the probe fails, or fail where it succeeds.
    /// </summary>
    public enum MilReadiness
    {
        /// <summary>No implementation yet. Not selectable.</summary>
        NotImplemented = 0,

        /// <summary>
        /// An implementation exists but MIL refused a context here. Selectable on purpose - this is
        /// the state a delivered sink is tested in - but never chosen automatically.
        /// </summary>
        Untested = 1,

        /// <summary>An implementation exists and MIL accepted a context. Selectable, and Auto picks it.</summary>
        Ready = 2,
    }

    /// <summary>What the recording path ended up using.</summary>
    public enum SinkChoice
    {
        None = 0,
        Ffmpeg = 1,
        Mil = 2,
    }

    /// <summary>
    /// Turns a preference and what is available into a choice, with a reason.
    ///
    /// MIL-free so the rules are assertions rather than something to discover by clicking. The one
    /// that matters most: <see cref="MilReadiness.Untested"/> is selectable but never automatic. An
    /// operator must not end up on a delivered-but-unproven encoder because a licence appeared;
    /// somebody has to ask for it.
    /// </summary>
    public static class VideoSinkPolicy
    {
        /// <summary>Whether the MIL option can be picked at all.</summary>
        public static bool MilSelectable(MilReadiness mil) => mil != MilReadiness.NotImplemented;

        /// <summary>
        /// What to record through. <paramref name="reason"/> is what to show: the backend's name on
        /// a choice, or why nothing can record.
        /// </summary>
        public static SinkChoice Choose(VideoSinkPreference preference, MilReadiness mil,
                                        bool ffmpegFound, out string reason)
        {
            switch (preference)
            {
                case VideoSinkPreference.Mil:
                    if (MilSelectable(mil))
                    {
                        // No fallback on an explicit choice. Falling through to ffmpeg here would
                        // make a broken MIL sink look like it works, which is the one outcome a
                        // test must not produce.
                        reason = mil == MilReadiness.Ready
                            ? "MIL/Mseq"
                            : "MIL/Mseq (untested on this installation)";
                        return SinkChoice.Mil;
                    }
                    reason = "The MIL sink is not implemented yet.";
                    return SinkChoice.None;

                case VideoSinkPreference.Ffmpeg:
                    if (ffmpegFound) { reason = "ffmpeg/libx264"; return SinkChoice.Ffmpeg; }
                    reason = FfmpegMissing;
                    return SinkChoice.None;

                default:
                    if (mil == MilReadiness.Ready) { reason = "MIL/Mseq"; return SinkChoice.Mil; }
                    if (ffmpegFound) { reason = "ffmpeg/libx264"; return SinkChoice.Ffmpeg; }
                    reason = mil == MilReadiness.Untested
                        ? FfmpegMissing + " The MIL sink is implemented but untested here; select it explicitly to try."
                        : FfmpegMissing;
                    return SinkChoice.None;
            }
        }

        private const string FfmpegMissing =
            "ffmpeg was not found. Install it or set the ffmpeg path in settings.";
    }
}
