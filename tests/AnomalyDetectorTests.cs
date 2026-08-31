using System.Collections.Generic;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The event-type path: a dropout is confirmed on a single frame, guarded by the coherence gate
    /// rather than by a frame count. Spec sections 6 and 8.
    /// </summary>
    public class AnomalyDetectorTests
    {
        const double Normal = 150;

        static AnomalyThresholds Fast() => new AnomalyThresholds
        {
            Depth = 0.10,
            Coherence = 0.80,
            DebounceFrames = 3,
            BaselineWindow = 9,
            BaselineWarmupFrames = 5,
        };

        /// <summary>A grid where every tile holds one pixel of the given value.</summary>
        static TileGrid Uniform(double value, long frame)
        {
            var g = new TileGrid { FrameNumber = frame };
            for (int i = 0; i < TileGrid.TileCount; i++)
                g.Accumulate(i, (long)value, (long)(value * value), 1);
            return g;
        }

        /// <summary>Tiles moving in different directions - content changing, not a dropout.</summary>
        static TileGrid Scattered(double value, long frame)
        {
            var g = new TileGrid { FrameNumber = frame };
            for (int i = 0; i < TileGrid.TileCount; i++)
            {
                double v = (i % 2 == 0) ? value * 0.4 : value * 1.6;
                g.Accumulate(i, (long)v, (long)(v * v), 1);
            }
            return g;
        }

        /// <summary>Feeds normal frames until the baseline is warm. Returns the next frame number.</summary>
        static long Warm(AnomalyDetector d, int frames = 8, long from = 1)
        {
            long n = from;
            for (int i = 0; i < frames; i++, n++)
                d.Observe(Uniform(Normal, n), n * 0.01);
            return n;
        }

        /// <summary>Everything Observe emitted while running the values as consecutive frames.</summary>
        static List<AnomalyEvent> Feed(AnomalyDetector d, long from, params double[] values)
        {
            var events = new List<AnomalyEvent>();
            long n = from;
            foreach (double v in values)
            {
                AnomalyEvent? e = d.Observe(Uniform(v, n), n * 0.01);
                if (e.HasValue) events.Add(e.Value);
                n++;
            }
            return events;
        }

        // ----- confirming a dropout -----

        [Fact]
        public void OneDarkFrame_IsConfirmedWithoutWaitingForASecond()
        {
            // A 1-3 frame event can never satisfy an N-frame rule, so the event path must not have
            // one. The coherence gate does the defending instead.
            var d = new AnomalyDetector(Fast());
            long n = Warm(d);

            List<AnomalyEvent> events = Feed(d, n, 0, Normal, Normal, Normal, Normal);

            Assert.Single(events);
            Assert.Equal(1, events[0].FrameCount);
            Assert.Equal(1.0, events[0].MaxDepth, 3);
        }

        [Fact]
        public void ADipShallowerThanTheThreshold_IsNotAnEvent()
        {
            var d = new AnomalyDetector(Fast());
            long n = Warm(d);

            // 145 against 150 is a depth of 0.033, under the 0.10 gate.
            Assert.Empty(Feed(d, n, 145, Normal, Normal, Normal, Normal));
        }

        [Fact]
        public void ContentChanging_IsRejectedByTheCoherenceGate()
        {
            // Tiles swing hard but disagree about direction. The median falls far enough to pass the
            // depth gate, and coherence is what must stop it - this is the false positive the whole
            // design turns on.
            var d = new AnomalyDetector(Fast());
            long n = Warm(d);

            var events = new List<AnomalyEvent>();
            AnomalyEvent? e = d.Observe(Scattered(Normal, n), n * 0.01);
            if (e.HasValue) events.Add(e.Value);
            for (long k = n + 1; k <= n + 5; k++)
            {
                e = d.Observe(Uniform(Normal, k), k * 0.01);
                if (e.HasValue) events.Add(e.Value);
            }

            Assert.Empty(events);
        }

        [Fact]
        public void AMultiFrameBlank_IsOneEventCountingItsFrames()
        {
            var d = new AnomalyDetector(Fast());
            long n = Warm(d);

            List<AnomalyEvent> events = Feed(d, n, 0, 0, 0, Normal, Normal, Normal, Normal);

            Assert.Single(events);
            Assert.Equal(3, events[0].FrameCount);
        }

        [Fact]
        public void AnEvent_ReportsWhenItStartedAndHowLongItLasted()
        {
            // "8 ms at depth 1.00, once" is the reporting unit the customer asked for, not
            // "it was in an anomalous state".
            var d = new AnomalyDetector(Fast());
            long n = Warm(d);

            List<AnomalyEvent> events = Feed(d, n, 0, 0, Normal, Normal, Normal, Normal);

            Assert.Single(events);
            Assert.Equal(n, events[0].StartFrame);
            Assert.Equal(n * 0.01, events[0].StartTimeSec, 6);
            // Two dark frames at 10 ms each: the screen was dark for 20 ms, not for the 10 ms
            // between their timestamps. The span under-reports by one frame period, and the number
            // that goes in the report has to be how long the panel was actually blank.
            Assert.Equal(20.0, events[0].DurationMs, 3);
        }

        // ----- debounce -----

        [Fact]
        public void TwoDipsInsideTheDebounce_AreCountedAsOneEvent()
        {
            var d = new AnomalyDetector(Fast());
            long n = Warm(d);

            List<AnomalyEvent> events = Feed(d, n, 0, Normal, 0, Normal, Normal, Normal, Normal);

            Assert.Single(events);
            Assert.Equal(2, events[0].FrameCount);
        }

        [Fact]
        public void TwoDipsBeyondTheDebounce_AreTwoEvents()
        {
            var d = new AnomalyDetector(Fast());
            long n = Warm(d);

            List<AnomalyEvent> events = Feed(d, n,
                0, Normal, Normal, Normal, Normal,
                0, Normal, Normal, Normal, Normal);

            Assert.Equal(2, events.Count);
            Assert.Equal(1, events[0].FrameCount);
            Assert.Equal(1, events[1].FrameCount);
        }

        [Fact]
        public void AnEventStillOpen_IsNotEmittedYet()
        {
            // The caller must not see a half-finished event; it is emitted once the debounce has
            // passed without recurrence.
            var d = new AnomalyDetector(Fast());
            long n = Warm(d);

            Assert.Empty(Feed(d, n, 0, 0));
            Assert.True(d.InEvent);
        }

        [Fact]
        public void Flush_ClosesAnOpenEventWhenTheGrabStops()
        {
            // Otherwise an anomaly that runs to the end of a grab is never reported.
            var d = new AnomalyDetector(Fast());
            long n = Warm(d);
            Feed(d, n, 0, 0);

            AnomalyEvent? e = d.Flush();

            Assert.True(e.HasValue);
            Assert.Equal(2, e.Value.FrameCount);
            Assert.False(d.InEvent);
        }

        // ----- frames the board dropped -----

        [Fact]
        public void AGapInTheFrameNumbers_DoesNotBecomeAnEvent()
        {
            // Frames the board could not deliver leave a hole. Reading that hole as a dropout is the
            // first false-positive path in this design.
            var d = new AnomalyDetector(Fast());
            long n = Warm(d);

            d.Observe(Uniform(Normal, n), n * 0.01);
            AnomalyEvent? e = d.Observe(Uniform(0, n + 5), (n + 5) * 0.01);

            Assert.False(e.HasValue);
            Assert.False(d.InEvent);
            Assert.Equal(1, d.FramesSkippedForGaps);
        }

        [Fact]
        public void AfterAGap_TheNextConsecutiveFrameIsJudgedNormally()
        {
            // The gap excuses one frame, not the rest of the run.
            var d = new AnomalyDetector(Fast());
            long n = Warm(d);

            d.Observe(Uniform(Normal, n + 5), (n + 5) * 0.01);
            List<AnomalyEvent> events = Feed(d, n + 6, 0, Normal, Normal, Normal, Normal);

            Assert.Single(events);
        }

        // ----- the baseline -----

        [Fact]
        public void BeforeTheBaselineIsWarm_NothingIsReported()
        {
            // With no history there is nothing to be a fraction of, and every frame would read as a
            // total blackout.
            var d = new AnomalyDetector(Fast());

            Assert.Empty(Feed(d, 1, Normal, 0, Normal));
        }

        [Fact]
        public void TheBaselineTracksTheNormalPicture()
        {
            var d = new AnomalyDetector(Fast());
            Warm(d);

            Assert.Equal(Normal, d.Baseline, 3);
        }

        [Fact]
        public void TheBaselineDoesNotFollowThePictureDownDuringAnEvent()
        {
            // A sustained blackout must not become the new normal. If dropout frames fed the running
            // median, a long enough fault would erase itself.
            var d = new AnomalyDetector(Fast());
            long n = Warm(d);

            for (long k = n; k < n + 12; k++)
                d.Observe(Uniform(0, k), k * 0.01);

            Assert.Equal(Normal, d.Baseline, 3);
            Assert.True(d.InEvent);
        }

        [Fact]
        public void TheBaselineFollowsALegitimateChangeInBrightness()
        {
            // The operator dims the panel, or the content is simply darker. Once a new level holds
            // it has to become the baseline, or every later frame reads as a dropout forever.
            var d = new AnomalyDetector(Fast());
            long n = Warm(d);

            // A slow ramp: each step is too shallow to trip the depth gate.
            double v = Normal;
            for (long k = n; k < n + 40; k++, v -= 1.5)
                d.Observe(Uniform(v, k), k * 0.01);

            Assert.True(d.Baseline < Normal - 20, "baseline did not follow the ramp down");
        }

        [Fact]
        public void Reset_ForgetsEverythingSoAChannelCanRestart()
        {
            var d = new AnomalyDetector(Fast());
            long n = Warm(d);
            Feed(d, n, 0, 0);

            d.Reset();

            Assert.False(d.InEvent);
            Assert.Equal(0.0, d.Baseline, 6);
            Assert.Equal(0, d.FramesSkippedForGaps);
        }

        [Fact]
        public void ANullGrid_IsIgnoredRatherThanThrowing()
        {
            var d = new AnomalyDetector(Fast());
            Assert.Null(d.Observe(null, 0));
        }
    }
}
