using System;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The depth threshold used to be chosen by hand: run a long grab, read the floor out of the
    /// log, compare it against the depth of real events, pick a number in between. These pin the
    /// rule that replaces that.
    ///
    /// The rule is a rate - the lowest depth whose exceedances fit a budget of false positives per
    /// hour - and one of these tests is why that is not sufficient on its own. At one an hour, a
    /// five-minute run allows 0.089 frames, so the rule reduces to "nothing may exceed" and a
    /// handful of outliers set the threshold by themselves. Keeping the frames around an event out
    /// of the population is what actually makes it steady.
    /// </summary>
    public class DepthProposalTests
    {
        const double Fps = 124.3;

        /// <summary>Thresholds that judge nothing an event, so every frame reaches the histogram.</summary>
        static AnomalyThresholds Quiet() => new AnomalyThresholds
        {
            Depth = 0.90,
            Coherence = 0.50,
            BaselineWindow = 31,
            BaselineWarmupFrames = 10,
            FalsePositiveBudgetPerHour = 1.0,
        };

        static TileGrid Uniform(double value, long frame)
        {
            var g = new TileGrid { FrameNumber = frame };
            for (int i = 0; i < TileGrid.TileCount; i++)
                g.Accumulate(i, (long)Math.Round(value), (long)Math.Round(value * value), 1);
            return g;
        }

        /// <summary>
        /// Feeds <paramref name="frames"/> frames whose level comes from <paramref name="level"/>.
        /// Levels alternate slightly by default so consecutive frames differ: a pair of identical
        /// frames has no deltas at all, coherence reads zero, and the frame would not be counted.
        /// </summary>
        static AnomalyDetector Fed(int frames, Func<int, double> level, AnomalyThresholds t = null)
        {
            var detector = new AnomalyDetector(t ?? Quiet());
            for (int i = 0; i < frames; i++)
                detector.Observe(Uniform(level(i), i + 1), (i + 1) / Fps);
            return detector;
        }

        /// <summary>Base level, jittered by a count so no two frames in a row match.</summary>
        static double Jitter(int i) => (i % 2 == 0) ? 200.0 : 198.0;

        [Fact]
        public void AQuietRunProposesSomethingJustAboveWhatItSaw()
        {
            AnomalyDetector detector = Fed(30000, Jitter);
            DepthProposal p = detector.Propose(1.0, Fps);

            Assert.True(p.IsUsable, p.ToString());
            Assert.True(p.Depth > 0, "a proposal has to be positive");
            Assert.True(p.Depth < 0.05, $"a quiet run should not need 5%; proposed {p.Depth}");
        }

        [Fact]
        public void ARunWithADeeperTailProposesMore()
        {
            AnomalyDetector quiet = Fed(30000, Jitter);
            AnomalyDetector noisy = Fed(30000, i =>
                i % 500 == 0 ? 200.0 * 0.90 : Jitter(i));      // a 10% dip twice a second-ish

            double a = quiet.Propose(1.0, Fps).Depth;
            double b = noisy.Propose(1.0, Fps).Depth;
            Assert.True(b > a, $"quiet proposed {a}, noisy proposed {b}");
        }

        [Fact]
        public void ATighterBudgetProposesMore()
        {
            AnomalyDetector detector = Fed(30000, i =>
                i % 500 == 0 ? 200.0 * 0.90 : Jitter(i));

            double loose = detector.Propose(1000.0, Fps).Depth;
            double tight = detector.Propose(0.01, Fps).Depth;
            Assert.True(tight >= loose, $"loose {loose}, tight {tight}");
        }

        /// <summary>Thresholds that do fire, so a fall produces a real event and a real shoulder.</summary>
        static AnomalyThresholds Firing() => new AnomalyThresholds
        {
            Depth = 0.10,
            Coherence = 0.50,
            DebounceFrames = 3,
            BaselineWindow = 31,
            BaselineWarmupFrames = 10,
            FalsePositiveBudgetPerHour = 1.0,
        };

        [Fact]
        public void TheFramesApproachingAnEventStayOutOfTheHistogram()
        {
            // Five events, each arrived at through a frame at 6% - under the 10% threshold, so
            // judged normal, and 20x the rest of the run. Measured on hardware one such frame read
            // 0.0994 against a floor of 0.011, and by itself made the floor look like it had no
            // margin. Absorbing it by allowance is not an option: at one an hour a run this long
            // allows 0.089 frames.
            const int frames = 40000;
            bool Shoulder(int i) => i % 8000 == 1000;
            bool Fall(int i) => i % 8000 >= 1001 && i % 8000 <= 1010;

            AnomalyDetector clean = Fed(frames, Jitter, Firing());
            AnomalyDetector withEvents = Fed(frames, i =>
                  Shoulder(i) ? 200.0 * 0.94
                : Fall(i)     ? 200.0 * 0.30
                : Jitter(i), Firing());

            DepthProposal a = clean.Propose(1.0, Fps);
            DepthProposal b = withEvents.Propose(1.0, Fps);

            Assert.True(a.IsUsable && b.IsUsable, $"clean {a}, with events {b}");
            Assert.True(Math.Abs(b.Depth - a.Depth) <= 0.01,
                $"clean proposed {a.Depth}, the approach frames moved it to {b.Depth}");
        }

        [Fact]
        public void AShortRunProposesNothing()
        {
            DepthProposal p = Fed(2000, Jitter).Propose(1.0, Fps);
            Assert.False(p.IsUsable);
            Assert.Contains("not enough", p.ToString());
        }

        [Fact]
        public void TheProposalSaysWhichBudgetTheRunCouldActuallyResolve()
        {
            // Ten minutes cannot demonstrate one false positive an hour. It has to say so rather
            // than answer as though it had.
            AnomalyDetector detector = Fed(30000, Jitter);       // about 240 s at 124.3 fps
            DepthProposal p = detector.Propose(1.0, Fps);

            Assert.InRange(p.MeasurableBudgetPerHour, 10.0, 20.0);
            Assert.True(p.AllowedFrames < 1.0,
                $"a run this short allows {p.AllowedFrames:F2} frames, so nothing may exceed it");
        }

        [Fact]
        public void TheRunsLengthComesFromEveryFrameItJudgedNotFromThePopulation()
        {
            // Measured on hardware: a five-minute grab judged 37,298 frames and put 221 of them in
            // the population, because the coherence gate let only those through. Taking the run's
            // length from the population made it look 170 times shorter, shrank the allowance by
            // the same factor, and would have proposed a threshold far too high.
            AnomalyDetector detector = Fed(30000, Jitter, Firing());
            DepthProposal p = detector.Propose(1.0, Fps);

            Assert.True(p.FramesJudged > p.FramesInPopulation,
                $"judged {p.FramesJudged}, population {p.FramesInPopulation}");
            Assert.InRange(p.FramesJudged, 29000, 30000);
            Assert.InRange(p.MeasurableBudgetPerHour, 10.0, 20.0);
        }

        [Fact]
        public void ProposeIsSafeBeforeAnythingHasBeenObserved()
        {
            DepthProposal p = new AnomalyDetector(Quiet()).Propose(1.0, Fps);
            Assert.False(p.IsUsable);
            Assert.Equal(0, p.FramesJudged);
        }

        [Fact]
        public void ProposeRejectsNonsenseArguments()
        {
            AnomalyDetector detector = Fed(30000, Jitter);
            Assert.False(detector.Propose(1.0, 0).IsUsable);      // no frame rate
            Assert.False(detector.Propose(0, Fps).IsUsable);       // no budget
        }

        [Fact]
        public void ResetForgetsTheHistogram()
        {
            AnomalyDetector detector = Fed(30000, Jitter);
            Assert.True(detector.HistogramFrames > 0);
            detector.Reset();
            Assert.Equal(0, detector.HistogramFrames);
            Assert.False(detector.Propose(1.0, Fps).IsUsable);
        }

        [Fact]
        public void ProvenanceSurvivesCopyFromUnclamped()
        {
            // CopyFrom clamps the thresholds, but provenance is a record of the past: clamping it
            // would make it a different record.
            var target = new AnomalyThresholds();
            target.CopyFrom(new AnomalyThresholds
            {
                CalibratedAt = "2026-09-02T12:39:00",
                CalibrationFrames = 223800,
                CalibrationFloor = 0.0184,
            });

            Assert.Equal("2026-09-02T12:39:00", target.CalibratedAt);
            Assert.Equal(223800, target.CalibrationFrames);
            Assert.Equal(0.0184, target.CalibrationFloor, 6);
        }
    }
}
