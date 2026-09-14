using System;
using System.Collections.Generic;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>Where a peak sat and how strong it was, before it became a box.</summary>
    public readonly struct PeakCandidate
    {
        public PeakCandidate(int row, int column, int channel, float logit,
                             float x, float y, float width, float height)
        {
            Row = row;
            Column = column;
            Channel = channel;
            Logit = logit;
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>Row in the heatmap grid.</summary>
        public int Row { get; }

        /// <summary>Column in the heatmap grid.</summary>
        public int Column { get; }

        /// <summary>Which class this peak belongs to.</summary>
        public int Channel { get; }

        /// <summary>
        /// The raw network output, before any sigmoid.
        ///
        /// Kept in logit space because that is where the threshold is applied: comparing against
        /// `logit(t)` once is the same decision as computing a sigmoid per cell and comparing
        /// against `t`, and the grid has tens of thousands of cells.
        /// </summary>
        public float Logit { get; }

        /// <summary>Box in model-input pixels.</summary>
        public float X { get; }
        public float Y { get; }
        public float Width { get; }
        public float Height { get; }

        public float Right => X + Width;
        public float Bottom => Y + Height;
        public float Area => Width <= 0f || Height <= 0f ? 0f : Width * Height;

        /// <summary>The confidence a caller reports, 0 to 1.</summary>
        public float Score => Sigmoid(Logit);

        /// <summary>
        /// Numerically stable sigmoid.
        ///
        /// The naive form overflows for a large negative logit. Splitting on the sign keeps the
        /// exponent negative in both branches.
        /// </summary>
        public static float Sigmoid(float logit) =>
            logit >= 0f
                ? 1f / (1f + MathF.Exp(-logit))
                : MathF.Exp(logit) / (1f + MathF.Exp(logit));

        /// <summary>The logit a probability threshold corresponds to.</summary>
        public static float LogitOf(float probability)
        {
            if (probability <= 0f) return float.NegativeInfinity;
            if (probability >= 1f) return float.PositiveInfinity;
            return MathF.Log(probability / (1f - probability));
        }
    }

    /// <summary>
    /// Orders peaks strongest first, and **breaks ties by position** rather than leaving it to
    /// the sort.
    ///
    /// A flat region of the heatmap puts the same logit on two to four neighbouring cells. Which
    /// one survives then depends on whether the sort happened to be stable, and the answer changes
    /// between frames - so a box that is not moving jitters by a cell. Ordering ties by `(row,
    /// column)` makes the choice a property of the data instead of the algorithm.
    /// </summary>
    public sealed class PeakByScore : IComparer<PeakCandidate>
    {
        public static readonly PeakByScore Instance = new PeakByScore();

        public int Compare(PeakCandidate a, PeakCandidate b)
        {
            int byLogit = b.Logit.CompareTo(a.Logit);       // descending
            if (byLogit != 0) return byLogit;
            int byRow = a.Row.CompareTo(b.Row);
            if (byRow != 0) return byRow;
            int byColumn = a.Column.CompareTo(b.Column);
            if (byColumn != 0) return byColumn;
            return a.Channel.CompareTo(b.Channel);
        }
    }

    /// <summary>
    /// Turns a CenterNet head's output into boxes: threshold, order, suppress overlaps.
    ///
    /// **No ONNX type appears here**, which is what lets it live in `Infrastructure/` and be
    /// tested. In the project this was ported from, the same 350 lines sat next to the inference
    /// session and could not be tested at all.
    ///
    /// The arithmetic is the published CenterNet decode - peak, offset, size - written here rather
    /// than copied, because this repository is public and the source it would have been copied
    /// from is not ours to publish.
    /// </summary>
    public sealed class CenterNetDecoder
    {
        private readonly List<PeakCandidate> _candidates = new List<PeakCandidate>(256);
        private readonly List<PeakCandidate> _kept = new List<PeakCandidate>(64);

        public CenterNetDecoder(float scoreThreshold = 0.3f, float iouThreshold = 0.5f,
                                int maxFindings = 32)
        {
            ScoreThreshold = Clamp01(scoreThreshold);
            IouThreshold = Clamp01(iouThreshold);
            MaxFindings = maxFindings < 1 ? 1 : maxFindings;
            LogitThreshold = PeakCandidate.LogitOf(ScoreThreshold);
        }

        /// <summary>Confidence a peak has to reach, 0 to 1.</summary>
        public float ScoreThreshold { get; }

        /// <summary>The same threshold in logit space, which is where it is applied.</summary>
        public float LogitThreshold { get; }

        /// <summary>How much two boxes may overlap before the weaker one is dropped.</summary>
        public float IouThreshold { get; }

        /// <summary>Most boxes one frame may produce.</summary>
        public int MaxFindings { get; }

        /// <summary>Peaks that cleared the threshold on the last call, before suppression.</summary>
        public int LastCandidates { get; private set; }

        /// <summary>
        /// Orders candidates and drops the ones that overlap a stronger box.
        ///
        /// Candidates come in as they were gathered; the ordering here is the one that matters,
        /// and it is deterministic - see <see cref="PeakByScore"/>.
        /// </summary>
        public int Suppress(IReadOnlyList<PeakCandidate> candidates, Span<PeakCandidate> into)
        {
            _kept.Clear();
            if (candidates == null || candidates.Count == 0) return 0;

            _candidates.Clear();
            for (int i = 0; i < candidates.Count; i++) _candidates.Add(candidates[i]);
            _candidates.Sort(PeakByScore.Instance);
            LastCandidates = _candidates.Count;

            for (int i = 0; i < _candidates.Count && _kept.Count < MaxFindings; i++)
            {
                PeakCandidate c = _candidates[i];
                bool covered = false;
                for (int k = 0; k < _kept.Count; k++)
                {
                    // Only a box of the same class suppresses: two classes may legitimately
                    // occupy the same place.
                    if (_kept[k].Channel != c.Channel) continue;
                    if (Iou(_kept[k], c) > IouThreshold) { covered = true; break; }
                }
                if (!covered) _kept.Add(c);
            }

            int n = Math.Min(_kept.Count, into.Length);
            for (int i = 0; i < n; i++) into[i] = _kept[i];
            return n;
        }

        /// <summary>
        /// Intersection over union.
        ///
        /// Edges are treated as lying between pixels, so a box from 0 to 10 and one from 10 to 20
        /// touch and do not overlap - the overlap is `max(0, min(rights) - max(lefts))` with no
        /// `+ 1` anywhere. The `+ 1` convention is where off-by-one bugs in this arithmetic live.
        /// </summary>
        public static float Iou(PeakCandidate a, PeakCandidate b)
        {
            float left = MathF.Max(a.X, b.X);
            float top = MathF.Max(a.Y, b.Y);
            float right = MathF.Min(a.Right, b.Right);
            float bottom = MathF.Min(a.Bottom, b.Bottom);

            float w = right - left;
            float h = bottom - top;
            if (w <= 0f || h <= 0f) return 0f;

            float overlap = w * h;
            float union = a.Area + b.Area - overlap;
            return union <= 0f ? 0f : overlap / union;
        }

        private static float Clamp01(float v) =>
            float.IsNaN(v) ? 0f : (v < 0f ? 0f : (v > 1f ? 1f : v));
    }
}
