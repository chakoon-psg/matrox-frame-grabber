using System;
using Matrox.MatroxImagingLibrary;

namespace MatroxFrameGrabber.Mil.Video
{
    /// <summary>
    /// One file a sink should write from the stream.
    ///
    /// Two of these describe the event-clip design: a segmented output at the full rate that a
    /// window can be cut out of, and a single long file at a quarter of it for the whole shift.
    /// </summary>
    public readonly struct VideoOutputSpec
    {
        /// <summary>
        /// Added to the file name to tell the outputs apart. Empty keeps the name a single-output
        /// recording has always had, which is what an operator looks for in the output folder.
        /// </summary>
        public readonly string Label;

        /// <summary>
        /// Frames this output wants: 1 for every frame, 4 for a quarter of the rate.
        ///
        /// It sets the rate the file declares - a quarter of 124.316 fps is 31.079, never the 30
        /// that was asked for. How the frames are selected is the backend's business: ffmpeg does
        /// it from -r inside one process, so the app still feeds every frame, while a sink that
        /// encodes per call has to skip.
        /// </summary>
        public readonly int EveryNthFrame;

        /// <summary>Seconds per file; 0 writes one file. Short segments close sooner, and a closed file is readable.</summary>
        public readonly double SegmentSeconds;

        /// <summary>Wanted seconds between keyframes; 0 leaves the encoder default (2.01 s at 124.3 fps).</summary>
        public readonly double KeyframeSeconds;

        /// <summary>Segments to keep before deleting the oldest; 0 keeps everything.</summary>
        public readonly int RetainSegments;

        public VideoOutputSpec(string label, int everyNthFrame = 1, double segmentSeconds = 0.0,
                               double keyframeSeconds = 0.0, int retainSegments = 0)
        {
            if (everyNthFrame < 1) throw new ArgumentOutOfRangeException(nameof(everyNthFrame));
            Label = label ?? string.Empty;
            EveryNthFrame = everyNthFrame;
            SegmentSeconds = segmentSeconds;
            KeyframeSeconds = keyframeSeconds;
            RetainSegments = retainSegments;
        }

        public bool IsSegmented => SegmentSeconds > 0.0;

        /// <summary>Every frame at the source rate, in one file. What "● Rec" has always done.</summary>
        public static VideoOutputSpec SingleFile(string label = "") => new VideoOutputSpec(label);
    }

    /// <summary>
    /// Everything a sink needs to start, with no reference to app settings so the contract can be
    /// handed to whoever implements another backend.
    /// </summary>
    public readonly struct VideoStreamSpec
    {
        /// <summary>
        /// A buffer of the geometry and type that will be fed. Read for size, bands and depth; the
        /// buffers passed to Feed must match it.
        /// </summary>
        public readonly MIL_ID Like;

        /// <summary>
        /// The rate every frame arrives at - the camera's ResultingFrameRate, not the rate it was
        /// asked for. See VideoRatePolicy.Declared: taking the configured AcquisitionFrameRate
        /// instead made a 120.0 s recording read as 81.07 s.
        /// </summary>
        public readonly double SourceFps;

        /// <summary>Folder the files go in. Must exist.</summary>
        public readonly string Folder;

        /// <summary>File-name stem, e.g. "Camera_0_ch0". A timestamp and the label are added.</summary>
        public readonly string BaseName;

        /// <summary>Output scale, 1.0 for original size. Below 1 the frames are resized first.</summary>
        public readonly double Scale;

        /// <summary>The files to write. At least one.</summary>
        public readonly VideoOutputSpec[] Outputs;

        public VideoStreamSpec(MIL_ID like, double sourceFps, string folder, string baseName,
                               double scale, VideoOutputSpec[] outputs)
        {
            if (outputs == null || outputs.Length == 0)
                throw new ArgumentException("a stream needs at least one output", nameof(outputs));
            Like = like;
            SourceFps = sourceFps;
            Folder = folder;
            BaseName = baseName;
            Scale = scale <= 0.0 ? 1.0 : scale;
            Outputs = outputs;
        }
    }
}
