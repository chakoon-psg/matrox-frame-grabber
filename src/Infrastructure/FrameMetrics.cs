using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// The two numbers a dropout decision rests on: how far the picture fell, and whether the tiles
    /// agree that it fell. Spec section 6.
    ///
    /// Pure arithmetic over <see cref="TileGrid"/>, deliberately free of any state — the running
    /// baseline and the state machine live in the detector, so these can be checked one call at a
    /// time.
    /// </summary>
    public static class FrameMetrics
    {
        /// <summary>
        /// How far this frame's representative brightness sits below the running baseline, as a
        /// fraction of it. A frame fully dark reads 1.
        ///
        /// A frame brighter than the baseline reads 0 rather than a negative number: brighter than
        /// usual is not a dropout, and a negative value would compare against a threshold in the
        /// wrong direction. No baseline yet also reads 0 — otherwise the first frames of every grab
        /// would each look like a total blackout.
        /// </summary>
        public static double Depth(double median, double baseline)
        {
            if (baseline <= 0)
                return 0.0;

            double depth = 1.0 - median / baseline;
            return depth <= 0 ? 0.0 : depth;
        }

        /// <summary>
        /// How much the tiles agree about which way the picture moved, from 0 to 1.
        ///
        /// <c>coh = |sum of deltas| / sum of |deltas|</c>. A screen going dark moves every tile the
        /// same way, so the two sums match and this reads 1. Content changing — a transition, a
        /// video playing, a map panning — moves tiles in different directions, the signed sum
        /// cancels while the magnitudes do not, and this falls toward 0.
        ///
        /// This is the whole of the false-positive defence. A single frame-wide average destroys the
        /// information; the tile grid has already computed it.
        ///
        /// A still picture reads 0, not 1. Both sums are zero there, and answering 1 would pair with
        /// whatever depth sensor noise produced and fire on a screen that never changed.
        ///
        /// Only tiles populated in both frames are compared. The sampled region can move under us —
        /// the operator redraws the ROI — and a tile present in one frame and missing from the other
        /// has no delta, not a delta the size of its own value.
        /// </summary>
        public static double Coherence(TileGrid before, TileGrid after)
        {
            if (before == null || after == null)
                return 0.0;

            double signed = 0;
            double magnitude = 0;

            for (int i = 0; i < TileGrid.TileCount; i++)
            {
                if (!before.IsPopulated(i) || !after.IsPopulated(i))
                    continue;

                double d = after.Mean(i) - before.Mean(i);
                signed += d;
                magnitude += Math.Abs(d);
            }

            return magnitude <= 0 ? 0.0 : Math.Abs(signed) / magnitude;
        }
    }
}
