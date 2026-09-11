using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Looks at one picture of an AVN screen and says what is on it.
    ///
    /// The division of labour against <see cref="AnomalyDetector"/> is the whole reason this
    /// exists, and it is a division of questions, not of quality:
    ///
    ///   - the tile detector answers **when** something changed, on every frame at 120 Hz,
    ///     out of brightness statistics that cost 200 us;
    ///   - an inspector answers **what is on the screen**, rarely, out of a model that costs
    ///     2.77 ms and cannot be asked 360 times a second across three channels (that is 100%
    ///     of the frame period - measured, see docs/adr/0002).
    ///
    /// So an inspector never runs in the acquisition hook. It runs on the four frames StillRing
    /// already keeps for a confirmed anomaly, on the stats tick, which is 11 ms once per event
    /// beside PNG writes that already cost 6-8 ms each.
    ///
    /// **Not thread safe.** Implementations own pre-allocated buffers and reuse them, which is
    /// what keeps a hot path free of allocation. One inspector per caller, or one lock.
    /// </summary>
    public interface IScreenInspector : IDisposable
    {
        /// <summary>
        /// What this is and where it came from, for the log and the tooltip.
        ///
        /// The factory writes this line at startup the way VideoSinkFactory does - the run has to
        /// say which weights produced its findings, because a file named for one resolution and
        /// built for another is a thing that has actually happened here.
        /// </summary>
        string Describe { get; }

        /// <summary>Width the picture has to be handed over at.</summary>
        int InputWidth { get; }

        /// <summary>Height the picture has to be handed over at.</summary>
        int InputHeight { get; }

        /// <summary>Kinds this inspector can report. Anything else it finds, it keeps to itself.</summary>
        AnomalyKind[] Reports { get; }

        /// <summary>
        /// Reads one picture and writes what it finds into <paramref name="into"/>, returning how
        /// many. Interleaved BGR, 8 bits a channel, <see cref="InputWidth"/> x
        /// <see cref="InputHeight"/>, rows packed with no padding.
        ///
        /// BGR and not RGB because that is the order a frame already arrives in and a swap is a
        /// whole pass over the picture. An implementation whose model wants RGB folds the swap
        /// into the weights of its first convolution, which is free.
        ///
        /// Coordinates come back in the **original frame** the picture was reduced from, which is
        /// why <see cref="BgrLetterbox"/> hands its mapping back rather than throwing it away.
        /// </summary>
        int Inspect(byte[] bgr, Span<ScreenFinding> into);
    }
}
