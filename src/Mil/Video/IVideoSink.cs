using System;
using System.Collections.Generic;
using Matrox.MatroxImagingLibrary;

namespace MatroxFrameGrabber.Mil.Video
{
    /// <summary>
    /// Something that turns grabbed frames into video files.
    ///
    /// <see cref="Feed"/> takes a <c>MIL_ID</c> rather than bytes on purpose, and that is the whole
    /// point of the interface. The ffmpeg backend has to read the frame out to host memory - 462 us
    /// per frame, measured - and pipe it. A MIL backend would hand the same buffer straight to
    /// MseqFeed and pay none of that. A contract in bytes would force the second backend into the
    /// first one's cost and then make it put the bytes back into a MIL buffer.
    ///
    /// Feed runs on the acquisition thread, inside an 8043 us frame period at the current operating
    /// point. An implementation that takes longer does not slow the preview down, it stalls the
    /// grab and shows up as M_PROCESS_FRAME_MISSED.
    ///
    /// Cutting a window out of what was written is deliberately not here - see IClipExtractor.
    /// ffmpeg can do it with a stream copy; whether MIL can is unknown, and the two should be
    /// swappable separately.
    /// </summary>
    public interface IVideoSink : IDisposable
    {
        /// <summary>What this is, for the log and the status line, e.g. "ffmpeg/libx264".</summary>
        string Name { get; }

        /// <summary>True between a successful Start and Stop.</summary>
        bool IsActive { get; }

        /// <summary>True when the backend died on its own. The channel surfaces this.</summary>
        bool Failed { get; }

        /// <summary>Why Start returned false, or why it failed since. Null when fine.</summary>
        string LastError { get; }

        /// <summary>The files being written, in the order of the spec's outputs.</summary>
        IReadOnlyList<string> FilePaths { get; }

        /// <summary>
        /// The rate each of those files declares, in the same order.
        ///
        /// Not the same as the rate frames arrive at: an output taking every fourth frame of
        /// 124.316 fps declares 31.079. Reported so a summary can name both, since the two
        /// disagreeing without anybody noticing is what made a 120 s recording read as 81 s.
        /// </summary>
        IReadOnlyList<double> FileRates { get; }

        /// <summary>
        /// The segment list of each output, in the same order as <see cref="FilePaths"/>, and null
        /// for an output written as one file.
        ///
        /// The list is how a reader learns which segments are closed and what span each holds -
        /// measured, they came out 1.994 to 2.019 s long against a nominal 2, so the boundaries
        /// cannot be derived from the file names.
        /// </summary>
        IReadOnlyList<string> SegmentListPaths { get; }

        /// <summary>What it did and what it lost.</summary>
        VideoSinkStats Stats { get; }

        /// <summary>
        /// Begins writing. Returns false with a reason rather than throwing, because the caller is
        /// a button and a failed recording must not take the grab with it.
        /// </summary>
        bool Start(VideoStreamSpec spec, out string error);

        /// <summary>
        /// Offers one frame. Called on the acquisition thread for every grabbed frame; an output
        /// that wants a fraction of them is the sink's business, not the caller's.
        /// </summary>
        void Feed(MIL_ID buffer, long frameNumber);

        /// <summary>Stops writing. Finalization may continue in the background.</summary>
        void Stop();

        /// <summary>
        /// Waits for a pending finalize, up to <paramref name="ms"/>. Called before the MIL system
        /// is freed, since finalization touches MIL buffers.
        /// </summary>
        void WaitFinalize(int ms);
    }
}
