using System;
using System.Collections.Generic;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>One anomaly as a rectangle on a lane.</summary>
    public readonly struct TimelineMark
    {
        /// <summary>Which kind, so the mark can be coloured by it - the item, not just the state.</summary>
        public readonly AnomalyKind Kind;

        /// <summary>True for something the onset-spread gate turned away.</summary>
        public readonly bool Rejected;

        /// <summary>True when the event was closed by the duration cap rather than by recovery.</summary>
        public readonly bool Truncated;

        /// <summary>Left edge in pixels from the lane's left.</summary>
        public readonly double Left;

        /// <summary>Width in pixels, never below <see cref="TimelineLayout.MinMarkPx"/>.</summary>
        public readonly double Width;

        /// <summary>Board seconds the event started, for the tooltip.</summary>
        public readonly double StartSec;

        public readonly double DurationMs;
        public readonly double MaxDeviation;
        public readonly long StartFrame;
        public readonly long EndFrame;

        public TimelineMark(AnomalyKind kind, bool rejected, bool truncated,
                            double left, double width, double startSec, double durationMs,
                            double maxDeviation, long startFrame, long endFrame)
        {
            Kind = kind;
            Rejected = rejected;
            Truncated = truncated;
            Left = left;
            Width = width;
            StartSec = startSec;
            DurationMs = durationMs;
            MaxDeviation = maxDeviation;
            StartFrame = startFrame;
            EndFrame = endFrame;
        }
    }

    /// <summary>An anomaly and what the detector decided about it, as the strip receives it.</summary>
    public readonly struct TimelineEvent
    {
        public readonly AnomalyEvent Event;

        /// <summary>Whether the onset-spread gate turned it away.</summary>
        public readonly bool Rejected;

        public TimelineEvent(AnomalyEvent e, bool rejected) { Event = e; Rejected = rejected; }
    }

    /// <summary>
    /// Places anomaly marks along a lane.
    ///
    /// Separate from the drawing because the two rules that matter came out of measured data and
    /// would otherwise be buried in canvas code. On the 30-minute static run, sixteen rejections
    /// fell inside 68 seconds with two detections among them: without a minimum width the shortest
    /// detection - 8.0 ms, which is 0.0013% of a ten-minute axis - is not drawn at all, and without
    /// an order the two detections disappear under the sixteen hatched rectangles they sit inside.
    /// The mark the strip exists for is the one both mistakes lose.
    /// </summary>
    public static class TimelineLayout
    {
        /// <summary>
        /// Narrowest a mark may be drawn. An 8 ms event on a 600 s axis is 0.0013% of the width,
        /// and the shortest events are the ones this strip exists to catch.
        /// </summary>
        public const double MinMarkPx = 5.0;

        /// <summary>
        /// Turns events into marks for a lane <paramref name="canvasPx"/> wide showing
        /// <paramref name="spanSec"/> seconds ending at <paramref name="windowEndSec"/>.
        ///
        /// Rejected marks come first in the result so a caller adding them in order draws them
        /// underneath. Events wholly outside the window are dropped; one that straddles an edge is
        /// clipped and keeps the edge it still has.
        /// </summary>
        public static List<TimelineMark> Place(IEnumerable<TimelineEvent> events,
                                               double windowEndSec, double spanSec, double canvasPx)
        {
            var marks = new List<TimelineMark>();
            if (events == null || spanSec <= 0.0 || canvasPx <= 0.0) return marks;

            double windowStart = windowEndSec - spanSec;
            double pxPerSec = canvasPx / spanSec;

            // Two passes so rejected marks land first without sorting: a comparison sort would
            // reorder equal keys unpredictably, and the order within a verdict is the arrival
            // order, which is what a reader follows.
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantRejected = pass == 0;
                foreach (TimelineEvent te in events)
                {
                    if (te.Rejected != wantRejected) continue;

                    double startSec = te.Event.StartTimeSec;
                    double endSec = startSec + te.Event.DurationMs / 1000.0;
                    if (endSec < windowStart || startSec > windowEndSec) continue;

                    double left = (startSec - windowStart) * pxPerSec;
                    double right = (endSec - windowStart) * pxPerSec;

                    // Widen before clipping, so a very short event near an edge still shows.
                    if (right - left < MinMarkPx) right = left + MinMarkPx;

                    if (left < 0.0) left = 0.0;
                    if (right > canvasPx) right = canvasPx;
                    if (right - left < MinMarkPx)
                    {
                        // Pushed against the right edge: keep the width and move the left edge in,
                        // rather than drawing a sliver that reads as nothing.
                        left = Math.Max(0.0, canvasPx - MinMarkPx);
                        right = canvasPx;
                    }
                    if (right <= left) continue;

                    marks.Add(new TimelineMark(
                        te.Event.Kind, te.Rejected, te.Event.Truncated,
                        left, right - left, startSec, te.Event.DurationMs,
                        te.Event.MaxDeviation, te.Event.StartFrame, te.Event.EndFrame));
                }
            }
            return marks;
        }

        /// <summary>
        /// Tick positions for a lane axis, as fractions of the width - 0 at the oldest edge.
        ///
        /// Fractions rather than pixels so the caller can label them without knowing the layout,
        /// and five of them because a ten-minute window divides into 2.5-minute steps that a
        /// reader can hold in their head.
        /// </summary>
        public static double[] AxisFractions(int ticks = 5)
        {
            if (ticks < 2) ticks = 2;
            var f = new double[ticks];
            for (int i = 0; i < ticks; i++) f[i] = (double)i / (ticks - 1);
            return f;
        }
    }
}
