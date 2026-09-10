namespace MatroxFrameGrabber.Mil.Video
{
    /// <summary>
    /// What a sink did and what it lost, read from the stats tick.
    ///
    /// Reported rather than inferred because none of it was visible when a recording lost 3.5% of
    /// its frames: the file looked plausible, and only ffprobe disagreed. A sink that cannot say
    /// how many frames it took, how many it refused, and what rate it claimed cannot be trusted to
    /// carry an anomaly's ten seconds.
    /// </summary>
    public readonly struct VideoSinkStats
    {
        /// <summary>Frames extracted and handed to the encoder.</summary>
        public readonly long FramesFed;

        /// <summary>
        /// Frames the feed skipped without extracting, because the encoder was already behind.
        /// This is the loss that happens under load, and a non-zero value means an event window
        /// cut from these files has holes in it.
        /// </summary>
        public readonly long FramesSkipped;

        /// <summary>Frames the encoder itself refused, a rarer path than <see cref="FramesSkipped"/>.</summary>
        public readonly long FramesDropped;

        /// <summary>
        /// The rate written into the files' headers - the file's time axis, whatever the camera was
        /// really doing. Kept separate from the measured rate because the two disagreeing is the
        /// failure worth catching.
        /// </summary>
        public readonly double DeclaredFps;

        /// <summary>Seconds recorded, frozen when the sink stops.</summary>
        public readonly double ElapsedSeconds;

        /// <summary>Mean microseconds per frame spent extracting. The frame period at 124.3 fps is 8043.</summary>
        public readonly double MeanFeedUs;

        /// <summary>Worst single extraction. One over the frame period costs a frame.</summary>
        public readonly double MaxFeedUs;

        /// <summary>
        /// Frames this sink was offered and did not want, because it takes every Nth.
        ///
        /// Separate from FramesSkipped on purpose: a session tier at 31 fps out of 124 turns away
        /// three frames in four *by design*, and counting those as skipped would make the log read
        /// "75% skipped" on a recording that lost nothing.
        /// </summary>
        public readonly long FramesNotWanted;

        public VideoSinkStats(long framesFed, long framesSkipped, long framesDropped,
                              double declaredFps, double elapsedSeconds,
                              double meanFeedUs, double maxFeedUs,
                              long framesNotWanted = 0)
        {
            FramesFed = framesFed;
            FramesSkipped = framesSkipped;
            FramesDropped = framesDropped;
            DeclaredFps = declaredFps;
            ElapsedSeconds = elapsedSeconds;
            MeanFeedUs = meanFeedUs;
            MaxFeedUs = maxFeedUs;
            FramesNotWanted = framesNotWanted;
        }

        /// <summary>Frames per second actually written, which should match <see cref="DeclaredFps"/>.</summary>
        public double WrittenFps => ElapsedSeconds > 0.0 ? FramesFed / ElapsedSeconds : 0.0;
    }
}
