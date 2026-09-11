using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// The inspector there is when there is no model.
    ///
    /// It finds nothing, and that is the whole feature. Removing the learned detector is meant to
    /// cost one ProjectReference line and a folder, and it only costs that if every caller
    /// already has something to call when the implementation is gone. The same object is what a
    /// missing weights file resolves to, so "switched off" and "not built" are one code path
    /// rather than two.
    ///
    /// MilSeqVideoSink stands in this tree unrun by the same construction.
    /// </summary>
    public sealed class NullScreenInspector : IScreenInspector
    {
        private static readonly AnomalyKind[] Nothing = Array.Empty<AnomalyKind>();

        public NullScreenInspector(string reason = null)
        {
            Reason = string.IsNullOrWhiteSpace(reason) ? "no model configured" : reason;
        }

        /// <summary>Why there is no inspector. The factory puts this in the log.</summary>
        public string Reason { get; }

        public string Describe => "no screen inspector - " + Reason;

        // Not zero: a caller sizing a letterbox off these would make one of no pixels. One says
        // "there is nothing to send here" without being a division by zero somewhere later.
        public int InputWidth => 1;
        public int InputHeight => 1;

        public AnomalyKind[] Reports => Nothing;

        public int Inspect(byte[] bgr, Span<ScreenFinding> into) => 0;

        public void Dispose() { }
    }
}
