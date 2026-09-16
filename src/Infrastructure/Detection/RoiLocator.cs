using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>What one look at the screen decided.</summary>
    public enum RoiLocatorOutcome
    {
        /// <summary>Nothing scored high enough. The region does not move.</summary>
        NothingFound,

        /// <summary>A fault is running on this channel. The region does not move.</summary>
        Frozen,

        /// <summary>What was found is where the region already is.</summary>
        InDeadband,

        /// <summary>Something different was found, and it has not been seen often enough yet.</summary>
        Confirming,

        /// <summary>The region moved.</summary>
        Moved,
    }

    /// <summary>
    /// One look's decision, and enough of the reasoning to put in a log line.
    /// </summary>
    public readonly struct RoiLocatorDecision
    {
        public RoiLocatorDecision(RoiLocatorOutcome outcome, ChannelRoi roi, float score,
                                  int streak, double iou, bool sizeChanged)
        {
            Outcome = outcome;
            Roi = roi;
            Score = score;
            Streak = streak;
            Iou = iou;
            SizeChanged = sizeChanged;
        }

        public RoiLocatorOutcome Outcome { get; }

        /// <summary>The region to use. The current one unless <see cref="Moved"/>.</summary>
        public ChannelRoi Roi { get; }

        /// <summary>Score of the finding this decision was made from, or 0.</summary>
        public float Score { get; }

        /// <summary>Consecutive looks that have agreed with this candidate, including this one.</summary>
        public int Streak { get; }

        /// <summary>Overlap between what was found and where the region already is.</summary>
        public double Iou { get; }

        /// <summary>
        /// Whether the move changed the region's SIZE beyond the tolerance - which is the case
        /// where the thresholds measured for this channel stop describing it. Position alone
        /// keeps the tile geometry and very nearly keeps the noise floor; size does not.
        /// </summary>
        public bool SizeChanged { get; }

        public bool Moved => Outcome == RoiLocatorOutcome.Moved;
    }

    /// <summary>
    /// Turns what the inspector found on the whole screen into the analysis region the tile
    /// detectors then measure - and, most of the time, refuses to.
    ///
    /// **The refusals are the feature.** Following a box is four lines; what this class is for is
    /// the five ways of not following it, because a region that moves when it should not takes the
    /// seven detectors with it and they go quiet rather than wrong. Nothing else in the app would
    /// report that: the lane is empty, the health is `Healthy`, and every threshold is being met -
    /// by pixels nobody chose.
    ///
    /// | | What it stops |
    /// |---|---|
    /// | **Hold when nothing is found** | A panel that goes dark is not found, and a region that cannot be found cannot be moved. This is what keeps a fault from pushing the region off the fault |
    /// | **Freeze while a fault runs** | The one case the line above does not cover - a fault the locator can still see through. The detectors get to keep looking at what they are judging |
    /// | **Deadband** | A box that lands a pixel or two off every look. Without it the even-pixel snap alone would walk the region back and forth for ever |
    /// | **Dwell** | One wrong box. Consecutive looks have to agree before anything happens |
    /// | **Step limit** | A teleport. A big move is made in several small ones, each of which has to survive the dwell again |
    ///
    /// **Position and size are not the same question** (<see cref="RoiLocatorDecision.SizeChanged"/>).
    /// A region that slides keeps the 8x8 tile geometry and very nearly keeps the noise floor the
    /// thresholds were measured against; a region that grows or shrinks changes the pixels per
    /// tile, and the floor with it. Both are allowed - the rig can be moved nearer as well as
    /// sideways - but the second is reported so the calibration can be marked as no longer
    /// describing this region.
    ///
    /// MIL-free and frame-free on purpose: it takes findings in acquired-frame pixels and returns
    /// a <see cref="ChannelRoi"/>, so all of the above is tested rather than argued about.
    /// </summary>
    public sealed class RoiLocator
    {
        private ChannelRoi _candidate;
        private bool _haveCandidate;
        private int _streak;

        /// <param name="minScore">
        /// A floor of this class's own, on top of the one the inspector already applied.
        ///
        /// **Defaults to none, and should usually stay there.** The model has an operating
        /// threshold that came with it (0.18, from the project it was ported from) and the
        /// inspector applies it; a second number here would be a knob nobody can set from
        /// anything measured - the region's whole point is that it is worked out rather than
        /// typed in. It is a parameter only so a test can pin it.
        /// </param>
        /// <param name="deadbandIou">
        /// Overlap with the current region at or above which nothing happens. 0.9 means "a box
        /// that agrees with the region to within about 5% of its width is the same box".
        /// </param>
        /// <param name="agreeIou">
        /// Overlap two consecutive candidates need for the second to count as agreeing with the
        /// first. Looser than the deadband would make the dwell meaningless.
        /// </param>
        /// <param name="dwellLooks">Consecutive agreeing looks before the region moves.</param>
        /// <param name="maxStepFraction">
        /// Most of the distance to the target one move may cover, as a fraction of the current
        /// region's size. The rest is covered by later moves, each of which pays the dwell again.
        /// </param>
        /// <param name="sizeTolerance">
        /// Relative size change that counts as "the same size" for
        /// <see cref="RoiLocatorDecision.SizeChanged"/>.
        /// </param>
        /// <param name="insetFraction">
        /// How much of the found box to trim off each edge before using it as a region. The box is
        /// around the panel; the region should probably be inside it, because bezel and reflection
        /// at the edges dilute exactly the dips the detectors measure (`BrightnessMeter` says so
        /// in its own words). **Defaults to none, because the right value is a measurement nobody
        /// has made yet** - not because zero is known to be right.
        /// </param>
        public RoiLocator(float minScore = 0.0f, double deadbandIou = 0.90, double agreeIou = 0.70,
                          int dwellLooks = 3, double maxStepFraction = 0.25,
                          double sizeTolerance = 0.05, double insetFraction = 0.0)
        {
            MinScore = minScore;
            DeadbandIou = Clamp01(deadbandIou);
            AgreeIou = Clamp01(agreeIou);
            DwellLooks = dwellLooks < 1 ? 1 : dwellLooks;
            MaxStepFraction = maxStepFraction <= 0.0 ? 1.0 : maxStepFraction;
            SizeTolerance = sizeTolerance < 0.0 ? 0.0 : sizeTolerance;
            InsetFraction = insetFraction < 0.0 ? 0.0 : (insetFraction > 0.4 ? 0.4 : insetFraction);
        }

        public float MinScore { get; }
        public double DeadbandIou { get; }
        public double AgreeIou { get; }
        public int DwellLooks { get; }
        public double MaxStepFraction { get; }
        public double SizeTolerance { get; }
        public double InsetFraction { get; }

        /// <summary>Looks taken since the last <see cref="Reset"/>.</summary>
        public long Looks { get; private set; }

        /// <summary>Looks that produced a finding worth considering.</summary>
        public long Seen { get; private set; }

        /// <summary>Times the region actually moved.</summary>
        public long Moves { get; private set; }

        /// <summary>
        /// Looks that were refused because a fault was running on this channel.
        ///
        /// Reported because it is the difference between "the model never saw the target" and
        /// "the model saw it every time and this class would not act on it" - which read the
        /// same in a log that only counts moves.
        /// </summary>
        public long FrozenLooks => Frozen;

        private long Frozen { get; set; }

        /// <summary>
        /// Forgets what it was confirming. Called when the operator sets the region by hand -
        /// **a person's rectangle is a new starting point, not something to argue with** - and at
        /// the start of a grab.
        /// </summary>
        public void Reset()
        {
            _haveCandidate = false;
            _streak = 0;
        }

        /// <summary>Also clears the counters. For a fresh run rather than a fresh rectangle.</summary>
        public void ResetRun()
        {
            Reset();
            Looks = Seen = Moves = Frozen = 0;
        }

        /// <summary>
        /// One look. <paramref name="findings"/> are in acquired-frame pixels, the same space
        /// <paramref name="current"/> is in.
        /// </summary>
        /// <param name="faultRunning">
        /// Whether any detector on this channel is inside a fault right now. When it is, the
        /// region does not move and what was being confirmed is dropped: confidence collected
        /// while the picture was faulty is not confidence about where the panel is.
        /// </param>
        public RoiLocatorDecision Look(ScreenFinding[] findings, int count, ChannelRoi current,
                                       int frameWidth, int frameHeight, bool faultRunning)
        {
            Looks++;

            // Counted BEFORE the freeze, and that ordering is the whole point. It used to come
            // after, so a run where the target was found on every look but a fault was also
            // running reported "0 with a finding" - and the summary went on to say the region
            // never moved because nothing was found. Both halves were false, and the one thing
            // that would have explained the run, that every look was frozen, was not counted at
            // all. A locator that is refusing to move has to say WHICH refusal it made.
            bool have = TryBest(findings, count, out ScreenFinding best);
            if (have) Seen++;

            if (faultRunning)
            {
                Frozen++;
                Reset();
                return new RoiLocatorDecision(RoiLocatorOutcome.Frozen, current,
                                              have ? best.Score : 0f, 0, 0.0, false);
            }

            if (!have)
            {
                Reset();
                return new RoiLocatorDecision(RoiLocatorOutcome.NothingFound, current, 0f, 0, 0.0, false);
            }

            ChannelRoi target = ToRegion(best, frameWidth, frameHeight);
            ChannelRoi currentRect = Resolve(current, frameWidth, frameHeight);
            double iou = Overlap(target, currentRect);

            if (iou >= DeadbandIou)
            {
                Reset();
                return new RoiLocatorDecision(RoiLocatorOutcome.InDeadband, current, best.Score, 0, iou, false);
            }

            // The dwell is about agreement between looks, not about repetition: three boxes that
            // each point somewhere different are three reasons not to move, not three votes.
            if (_haveCandidate && Overlap(target, _candidate) >= AgreeIou)
                _streak++;
            else
                _streak = 1;

            _candidate = target;
            _haveCandidate = true;

            if (_streak < DwellLooks)
                return new RoiLocatorDecision(RoiLocatorOutcome.Confirming, current, best.Score, _streak, iou, false);

            ChannelRoi step = Step(currentRect, target, frameWidth, frameHeight);
            bool sizeChanged = SizeDiffers(currentRect, step);

            Reset();
            Moves++;
            return new RoiLocatorDecision(RoiLocatorOutcome.Moved, step, best.Score, DwellLooks, iou, sizeChanged);
        }

        /// <summary>
        /// The highest-scoring finding at or above <see cref="MinScore"/>.
        ///
        /// Highest score rather than largest box: a model that reports the panel and a reflection
        /// of it should be believed about which is which, and area would pick whichever happened
        /// to be nearer the camera.
        /// </summary>
        private bool TryBest(ScreenFinding[] findings, int count, out ScreenFinding best)
        {
            best = default;
            if (findings == null || count <= 0) return false;

            bool any = false;
            for (int i = 0; i < count && i < findings.Length; i++)
            {
                ScreenFinding f = findings[i];
                if (f.Score < MinScore || f.Area <= 0f) continue;
                if (!any || f.Score > best.Score) { best = f; any = true; }
            }
            return any;
        }

        /// <summary>
        /// A finding's box as a region: inset, rounded, clamped to the frame and snapped to the
        /// even grid. Snapped here rather than by the caller so that what this class compares
        /// against the current region is exactly what would be applied - otherwise the deadband
        /// is computed on one rectangle and the snap produces another, which is how a region
        /// oscillates by two pixels for ever.
        /// </summary>
        private ChannelRoi ToRegion(ScreenFinding f, int frameWidth, int frameHeight)
        {
            double insetX = f.Width * InsetFraction;
            double insetY = f.Height * InsetFraction;

            double x0 = f.X + insetX, y0 = f.Y + insetY;
            double x1 = f.Right - insetX, y1 = f.Bottom - insetY;

            int left = (int)Math.Round(x0), top = (int)Math.Round(y0);
            int right = (int)Math.Round(x1), bottom = (int)Math.Round(y1);

            if (left < 0) left = 0;
            if (top < 0) top = 0;
            if (right > frameWidth) right = frameWidth;
            if (bottom > frameHeight) bottom = frameHeight;
            if (right - left < ChannelRoi.MinEditableSize) right = left + ChannelRoi.MinEditableSize;
            if (bottom - top < ChannelRoi.MinEditableSize) bottom = top + ChannelRoi.MinEditableSize;

            return new ChannelRoi(left, top, right - left, bottom - top).Snap(frameWidth, frameHeight);
        }

        /// <summary>
        /// The region, with "no rectangle drawn" resolved to the whole frame so overlap means
        /// something. A first move out of full frame is the commissioning case and has to be
        /// allowed to happen, so this must not be treated as "no current region".
        /// </summary>
        private static ChannelRoi Resolve(ChannelRoi roi, int frameWidth, int frameHeight) =>
            roi.IsFullFrame ? new ChannelRoi(0, 0, frameWidth, frameHeight) : roi;

        /// <summary>
        /// One move towards the target, no further than <see cref="MaxStepFraction"/> of the
        /// current size in any direction.
        ///
        /// Each edge is limited separately rather than the centre being interpolated: a region
        /// that is correct on three sides and wrong on one should fix the one.
        /// </summary>
        private ChannelRoi Step(ChannelRoi current, ChannelRoi target, int frameWidth, int frameHeight)
        {
            int limitX = (int)Math.Max(2.0, current.Width * MaxStepFraction);
            int limitY = (int)Math.Max(2.0, current.Height * MaxStepFraction);

            int left = Toward(current.OffsetX, target.OffsetX, limitX);
            int top = Toward(current.OffsetY, target.OffsetY, limitY);
            int right = Toward(current.OffsetX + current.Width, target.OffsetX + target.Width, limitX);
            int bottom = Toward(current.OffsetY + current.Height, target.OffsetY + target.Height, limitY);

            if (right - left < ChannelRoi.MinEditableSize) right = left + ChannelRoi.MinEditableSize;
            if (bottom - top < ChannelRoi.MinEditableSize) bottom = top + ChannelRoi.MinEditableSize;

            return new ChannelRoi(left, top, right - left, bottom - top).Snap(frameWidth, frameHeight);
        }

        private static int Toward(int from, int to, int limit)
        {
            int delta = to - from;
            if (delta > limit) delta = limit;
            if (delta < -limit) delta = -limit;
            return from + delta;
        }

        private bool SizeDiffers(ChannelRoi a, ChannelRoi b)
        {
            if (a.Width <= 0 || a.Height <= 0) return true;
            double dw = Math.Abs(b.Width - a.Width) / (double)a.Width;
            double dh = Math.Abs(b.Height - a.Height) / (double)a.Height;
            return dw > SizeTolerance || dh > SizeTolerance;
        }

        /// <summary>
        /// Intersection over union. Edges are edges, not pixel indices - a region 100 wide at 0
        /// ends at 100 - which is the same convention <see cref="ScreenFinding.Right"/> uses.
        /// </summary>
        public static double Overlap(ChannelRoi a, ChannelRoi b)
        {
            long ax1 = a.OffsetX, ay1 = a.OffsetY, ax2 = a.OffsetX + a.Width, ay2 = a.OffsetY + a.Height;
            long bx1 = b.OffsetX, by1 = b.OffsetY, bx2 = b.OffsetX + b.Width, by2 = b.OffsetY + b.Height;

            long w = Math.Min(ax2, bx2) - Math.Max(ax1, bx1);
            long h = Math.Min(ay2, by2) - Math.Max(ay1, by1);
            if (w <= 0 || h <= 0) return 0.0;

            long inter = w * h;
            long union = (long)a.Width * a.Height + (long)b.Width * b.Height - inter;
            return union <= 0 ? 0.0 : inter / (double)union;
        }

        private static double Clamp01(double v) => v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v);

        public override string ToString() =>
            $"locator: {Looks} look(s), {Seen} with a finding, {Moves} move(s)"
          + (Frozen > 0 ? $", {Frozen} frozen by a running fault" : string.Empty) + "; "
          + $"score >= {MinScore:0.##}, deadband IoU {DeadbandIou:0.##}, dwell {DwellLooks}, "
          + $"step <= {MaxStepFraction:P0}";
    }
}
