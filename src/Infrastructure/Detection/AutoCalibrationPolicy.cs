using System;
using System.Globalization;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>Why a run was or was not allowed to move a threshold.</summary>
    public enum AutoCalibrationVerdict
    {
        /// <summary>The option is off. The default, and the only state a shipped rig should be in.</summary>
        SwitchedOff = 0,

        /// <summary>Adopted. The threshold moved and the run that moved it is recorded beside it.</summary>
        Adopted = 1,

        /// <summary>The run was too short to resolve the budget it is meant to defend.</summary>
        TooShort = 2,

        /// <summary>Something fired. The floor this would adopt from is not a floor.</summary>
        NotQuiet = 3,

        /// <summary>Frames went missing, so the run does not describe the rig at its operating point.</summary>
        Degraded = 4,

        /// <summary>There was nothing to propose from.</summary>
        NoProposal = 5,

        /// <summary>The move was too large to make without a person looking at it.</summary>
        TooBigAChange = 6,

        /// <summary>The move was too small to be worth writing down.</summary>
        NotWorthIt = 7,
    }

    /// <summary>What the policy decided, and the sentence that goes in the log.</summary>
    public readonly struct AutoCalibrationDecision
    {
        public AutoCalibrationDecision(AutoCalibrationVerdict verdict, double from, double to,
                                       double floor, long frames, string why)
        {
            Verdict = verdict;
            From = from;
            To = to;
            Floor = floor;
            Frames = frames;
            Why = why ?? string.Empty;
        }

        public AutoCalibrationVerdict Verdict { get; }
        public double From { get; }
        public double To { get; }
        public double Floor { get; }
        public long Frames { get; }
        public string Why { get; }

        public bool Adopted => Verdict == AutoCalibrationVerdict.Adopted;

        public override string ToString()
        {
            var inv = CultureInfo.InvariantCulture;
            return Adopted
                ? string.Format(inv,
                    "auto-calibration adopted - {0:0.####} -> {1:0.####} ({2:0.0}x the {3:0.####} "
                  + "floor of {4} quiet frames)", From, To, To / Math.Max(Floor, 1e-9), Floor, Frames)
                : string.Format(inv, "auto-calibration declined - {0}", Why);
        }
    }

    /// <summary>
    /// Decides whether a run earned the right to move a threshold by itself.
    ///
    /// **It exists because nobody is at the bench.** The manual path asks a person to confirm the
    /// run was on a healthy panel, which is the judgement automation cannot make; everything here
    /// is an attempt to replace that judgement with conditions a machine can check. It is off by
    /// default and should stay off on a rig somebody can reach.
    ///
    /// The condition that matters most is **nothing fired**, and not for the obvious reason.
    ///
    /// A quiet run is also the only run whose floor is independent of the threshold being
    /// adjusted. `MaxCoherentNormalDepth` is measured over frames outside an event, so raising a
    /// threshold means fewer events, which means fewer frames excluded, which can raise the
    /// measured floor, which raises the threshold again - a ratchet that walks the detector deaf
    /// over a weekend of unattended runs. When nothing fired, no frame was excluded, and the floor
    /// describes the whole run whatever the threshold was. That breaks the loop.
    ///
    /// The value adopted is **twice the floor**, not the histogram proposal. The proposal picks
    /// the depth where the false-positive budget is met exactly, which on the 2026-09-11 hour sat
    /// 1.04x above the loudest noise measured - correct statistics and no margin. Doubling is what
    /// this repo used when it set the threshold by hand.
    /// </summary>
    public sealed class AutoCalibrationPolicy
    {
        /// <summary>
        /// The multiple of the measured floor to adopt.
        ///
        /// Not a setting. It is the margin the project already decided on, and a knob here would
        /// invite somebody to turn it down to 1.1 and call the result calibrated.
        /// </summary>
        public const double FloorMultiple = 2.0;

        /// <summary>
        /// How much of a change is worth making. Below this the threshold is left alone.
        ///
        /// Without it every run rewrites the settings file with a value a fraction of a percent
        /// away, and the provenance stamp - which run set this - stops meaning anything.
        /// </summary>
        public const double MinRelativeChange = 0.05;

        public AutoCalibrationPolicy(bool enabled, double minMinutes, double maxChangeFactor)
        {
            Enabled = enabled;
            MinMinutes = minMinutes < 1.0 ? 1.0 : (minMinutes > 1440.0 ? 1440.0 : minMinutes);
            MaxChangeFactor = maxChangeFactor < 1.1 ? 1.1
                            : (maxChangeFactor > 100.0 ? 100.0 : maxChangeFactor);
        }

        public bool Enabled { get; }

        /// <summary>
        /// How long a run has to be before its floor is allowed to move anything.
        ///
        /// Five minutes resolves a false-positive budget only to 11.8/hour against a target of 1;
        /// an hour resolves it to 1. Anything shorter than this is measuring a quieter rig than
        /// the one that will run overnight.
        /// </summary>
        public double MinMinutes { get; }

        /// <summary>
        /// The largest factor a single run may move a threshold by.
        ///
        /// A big jump is not a calibration, it is a changed rig - somebody moved a light, or the
        /// exposure changed, or a camera came loose. Those are worth a person looking, so the
        /// policy declines and says so instead of following the rig wherever it went.
        /// </summary>
        public double MaxChangeFactor { get; }

        /// <summary>
        /// Judges one finished run.
        ///
        /// Every rejection names itself, because a feature that silently does nothing is
        /// indistinguishable from one that is broken - and this one will spend most of its life
        /// doing nothing.
        /// </summary>
        public AutoCalibrationDecision Decide(double currentThreshold, double floor,
                                              long framesJudged, double fps,
                                              long anomalies, long rejectedForSpread,
                                              long framesMissed, bool proposalUsable)
        {
            var inv = CultureInfo.InvariantCulture;

            if (!Enabled)
                return No(AutoCalibrationVerdict.SwitchedOff, currentThreshold, floor, framesJudged,
                          "the option is off");

            // Duration first, because it is the one an operator can act on. A short run also
            // fails proposalUsable - the proposal wants 10,000 frames - and reporting "nothing to
            // propose from" for a run that was simply too short sends the reader looking for a
            // fault in the panel.
            double minutes = fps > 1.0 ? framesJudged / fps / 60.0 : 0.0;
            if (minutes < MinMinutes)
                return No(AutoCalibrationVerdict.TooShort, currentThreshold, floor, framesJudged,
                          string.Format(inv, "the run was {0:0.0} min and {1:0.0} is the minimum - "
                                           + "a short run measures a quieter rig than the one that "
                                           + "has to survive the night", minutes, MinMinutes));

            if (!proposalUsable)
                return No(AutoCalibrationVerdict.NoProposal, currentThreshold, floor, framesJudged,
                          "the run was long enough but had nothing to propose from");

            // The one that also keeps the floor honest: a run with an event in it has frames
            // excluded from the floor, and which frames depends on the threshold being adjusted.
            if (anomalies > 0 || rejectedForSpread > 0)
                return No(AutoCalibrationVerdict.NotQuiet, currentThreshold, floor, framesJudged,
                          string.Format(inv, "{0} anomal(ies) and {1} turned away for onset spread "
                                           + "- frames inside an event do not count toward the "
                                           + "floor, so this floor depends on the threshold it "
                                           + "would be setting", anomalies, rejectedForSpread));

            if (framesMissed > 0)
                return No(AutoCalibrationVerdict.Degraded, currentThreshold, floor, framesJudged,
                          string.Format(inv, "{0} frame(s) went missing, so the run does not "
                                           + "describe the rig at its operating point", framesMissed));

            if (floor <= 0.0)
                return No(AutoCalibrationVerdict.NoProposal, currentThreshold, floor, framesJudged,
                          "the measured floor is zero, which is not a measurement");

            double adopt = floor * FloorMultiple;

            double factor = currentThreshold > 0.0
                ? Math.Max(adopt / currentThreshold, currentThreshold / adopt)
                : double.MaxValue;
            if (factor > MaxChangeFactor)
                return No(AutoCalibrationVerdict.TooBigAChange, currentThreshold, floor, framesJudged,
                          string.Format(inv, "{0:0.####} -> {1:0.####} is {2:0.0}x and {3:0.0}x is "
                                           + "the limit - a jump that size is a changed rig, not a "
                                           + "calibration", currentThreshold, adopt, factor,
                                        MaxChangeFactor));

            if (currentThreshold > 0.0
                && Math.Abs(adopt - currentThreshold) / currentThreshold < MinRelativeChange)
                return No(AutoCalibrationVerdict.NotWorthIt, currentThreshold, floor, framesJudged,
                          string.Format(inv, "{0:0.####} is within {1:0}% of {2:0.####}, so it is "
                                           + "left alone", adopt, MinRelativeChange * 100.0,
                                        currentThreshold));

            return new AutoCalibrationDecision(AutoCalibrationVerdict.Adopted,
                                               currentThreshold, adopt, floor, framesJudged, string.Empty);
        }

        private static AutoCalibrationDecision No(AutoCalibrationVerdict v, double from, double floor,
                                                  long frames, string why) =>
            new AutoCalibrationDecision(v, from, from, floor, frames, why);
    }
}
