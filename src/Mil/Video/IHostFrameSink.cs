using Matrox.MatroxImagingLibrary;

namespace MatroxFrameGrabber.Mil.Video
{
    /// <summary>
    /// A sink that can be handed a frame somebody else has already read out of MIL.
    ///
    /// Deliberately not part of <see cref="IVideoSink"/>. That contract takes a <c>MIL_ID</c>
    /// precisely so a MIL backend pays nothing for a host read, and it is the slice handed to the
    /// board's supplier - putting host bytes in it would hand them a method their sink has no use
    /// for. This interface is for the case the app actually runs: two ffmpeg sinks on one channel,
    /// where reading the frame out twice cost 60-67 frames of 37,300 per channel (measured
    /// 2026-09-10). A sink that does not implement it keeps being fed the buffer.
    /// </summary>
    public interface IHostFrameSink
    {
        /// <summary>
        /// Where its frames come from. Set before Start; null means the sink reads for itself.
        /// The extractor belongs to whoever set it and outlives the sink's Stop.
        /// </summary>
        FrameExtractor SharedFrames { get; set; }

        /// <summary>
        /// Whether this sink wants this particular frame right now - it has room, and the frame
        /// belongs to it.
        ///
        /// Both halves matter and the caller asks before reading anything out. Nothing is extracted
        /// while every encoder is behind, which keeps the live view ahead of the disk; and nothing
        /// is extracted for a sink taking every fourth frame on the three frames it does not want.
        /// </summary>
        bool WantsFrame(long frameNumber);

        /// <summary>
        /// Offers a frame the caller holds a hold on. The sink adds its own hold before queueing it
        /// and releases when its writer is done; the caller releases its own afterwards.
        ///
        /// Null means the caller had no frame to give - the pool was dry, or nothing was read out
        /// because no sink had room - and the sink counts that as a skipped frame, which is how a
        /// shortage stays visible instead of turning into a shorter file nobody questions.
        /// </summary>
        bool FeedShared(byte[] frame, long frameNumber);
    }
}
