using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Looks at one picture and says what is on it.
    ///
    /// **A different question from the tile detectors', and that is why it is a separate
    /// contract.** `AnomalyDetector` and the six sustained detectors answer *when* something
    /// deviated, 120 times a second, from 64 numbers. This answers *what is there*, rarely, from
    /// the picture. `CONTEXT.md` keeps the two apart on purpose - the cost forces it (a tile
    /// reduction is 200 us, an inference is milliseconds).
    ///
    /// **Not thread safe.** An implementation pre-allocates its buffers and reuses them, which is
    /// how allocation stays off the hot path - so one instance per caller, or one lock.
    ///
    /// The contract names no ONNX, no OpenCV and no MIL type, which is what lets it live in
    /// `Infrastructure/` and be tested. Pulling the whole feature out is deleting one project
    /// reference; the factory then hands back <see cref="NullScreenInspector"/> and nothing else
    /// changes.
    /// </summary>
    public interface IScreenInspector : IDisposable
    {
        /// <summary>
        /// What this inspector is, in one line, for the log and the tooltip.
        ///
        /// A finding is worthless without knowing which weights produced it, and the weights are
        /// a file on disk that can be swapped without the app noticing.
        /// </summary>
        string Describe { get; }

        /// <summary>Width the model takes, in pixels.</summary>
        int InputWidth { get; }

        /// <summary>Height the model takes, in pixels.</summary>
        int InputHeight { get; }

        /// <summary>
        /// The kinds this inspector is able to report. Nothing outside this set will come back.
        ///
        /// Here so the settings window can say which kinds are live *because a model is loaded*
        /// rather than because a detector was written.
        /// </summary>
        AnomalyKind[] Reports { get; }

        /// <summary>
        /// Inspects one picture, already letterboxed to <see cref="InputWidth"/> x
        /// <see cref="InputHeight"/>, interleaved BGR, no row padding.
        /// </summary>
        /// <remarks>
        /// <paramref name="into"/> cannot be `stackalloc`ed: <see cref="ScreenFinding"/> carries
        /// the model's own label as a string, which makes it a managed type. Callers pre-allocate
        /// an array once and reuse it - which is what the rest of this design does anyway, and
        /// better than a stack buffer on a path that runs beside acquisition.
        /// </remarks>
        /// <returns>How many findings were written into <paramref name="into"/>.</returns>
        int Inspect(byte[] bgr, Span<ScreenFinding> into);
    }

    /// <summary>
    /// The inspector that is always there and never finds anything.
    ///
    /// This is what makes the removal cost nearly zero: with no model file, or with the ONNX
    /// project deleted, the factory returns this and every call site keeps compiling and running
    /// unchanged. `MilSeqVideoSink` earns its place the same way - a shape that costs nothing
    /// until something fills it.
    /// </summary>
    public sealed class NullScreenInspector : IScreenInspector
    {
        private readonly string _why;

        public NullScreenInspector(string why)
        {
            _why = string.IsNullOrWhiteSpace(why) ? "no reason given" : why;
        }

        public string Describe => $"no inspector - {_why}";
        public int InputWidth => 0;
        public int InputHeight => 0;
        public AnomalyKind[] Reports => Array.Empty<AnomalyKind>();

        public int Inspect(byte[] bgr, Span<ScreenFinding> into) => 0;

        public void Dispose() { }
    }
}
