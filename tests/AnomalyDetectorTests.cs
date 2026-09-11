using System;
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
            Assert.Equal(1.0, events[0].MaxDeviation, 3);
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
    
        /// <summary>
        /// The caller may hand the same grid back frame after frame, and detection must survive it.
        ///
        /// This is not a hypothetical. The reducer on the acquisition path reuses one grid per
        /// channel -- allocating one per frame is what it was written to avoid -- and the detector
        /// used to keep that object as its previous frame. The previous frame and the current one
        /// were then the same object, every tile delta was zero, coherence was zero, and since
        /// coherence gates entry no event could ever open. On hardware the detector reported
        /// nothing while the exposure was halved under it. Every other test here hands Observe a
        /// fresh grid, which is the one pattern where holding the reference happens to work.
        /// </summary>
        [Fact]
        public void ADropoutIsFoundEvenWhenTheCallerReusesOneGrid()
        {
            var detector = new AnomalyDetector(Fast());
            var scratch = new TileGrid();
            var emitted = new List<AnomalyEvent>();

            void Feed1(double value, long frame)
            {
                // The reducer's exact pattern: one grid, reset and refilled in place.
                scratch.Reset();
                scratch.FrameNumber = frame;
                for (int i = 0; i < TileGrid.TileCount; i++)
                    scratch.Accumulate(i, (long)value, (long)(value * value), 1);

                AnomalyEvent? closed = detector.Observe(scratch, frame * 0.01);
                if (closed.HasValue) emitted.Add(closed.Value);
            }

            long n = 1;
            for (; n <= 8; n++) Feed1(Normal, n);           // warm the baseline

            Feed1(Normal * 0.1, n++);                       // the dropout
            Assert.True(detector.LastCoherence > 0.99,
                "a uniform fall must agree across every tile; " +
                $"coherence was {detector.LastCoherence}");
            Assert.True(detector.InEvent, "the event should be open on the dropout frame");

            for (int i = 0; i < 5; i++) Feed1(Normal, n++); // clear frames close it
            Assert.False(detector.InEvent);

            AnomalyEvent one = Assert.Single(emitted);
            Assert.Equal(1, one.FrameCount);
            Assert.True(one.MaxDeviation > 0.8, $"depth was {one.MaxDeviation}");
        }

        /// <summary>Thresholds with a short event cap, for the truncation rules.</summary>
        static AnomalyThresholds Capped(int cap) => new AnomalyThresholds
        {
            Depth = 0.10,
            Coherence = 0.80,
            DebounceFrames = 3,
            BaselineWindow = 9,
            BaselineWarmupFrames = 5,
            MaxEventFrames = cap,
        };

        [Fact]
        public void ASustainedFallIsClosedAtTheCapAndMarkedStillRunning()
        {
            var detector = new AnomalyDetector(Capped(6));
            long n = Warm(detector);

            AnomalyEvent? emitted = null;
            for (int i = 0; i < 6; i++, n++)
            {
                AnomalyEvent? closed = detector.Observe(Uniform(Normal * 0.5, n), n * 0.01);
                if (closed.HasValue) emitted = closed;
            }

            Assert.True(emitted.HasValue, "the cap should have closed the event");
            Assert.True(emitted.Value.Truncated);
            Assert.Equal(6, emitted.Value.FrameCount);
            Assert.False(detector.InEvent);
            Assert.Contains("still running", emitted.Value.ToString());
        }

        [Fact]
        public void AFallShorterThanTheCapIsNotMarkedStillRunning()
        {
            var detector = new AnomalyDetector(Capped(100));
            long n = Warm(detector);

            List<AnomalyEvent> events = Feed(detector, n, Normal * 0.5, Normal, Normal, Normal, Normal);

            AnomalyEvent one = Assert.Single(events);
            Assert.False(one.Truncated);
            Assert.Equal(1, one.FrameCount);
        }

        /// <summary>
        /// The failure the cap exists for. On hardware a channel entered an event on a sustained 8%
        /// fall that came from moving content, the baseline froze, depth never recovered, and since
        /// nothing is emitted while an event is open the channel reported nothing for the rest of
        /// the run -- it missed all seven true events that followed, which two other channels
        /// caught. One long event was never the problem; going blind was.
        /// </summary>
        [Fact]
        public void DetectionResumesAfterTheCapInsteadOfGoingBlind()
        {
            var detector = new AnomalyDetector(Capped(6));
            long n = Warm(detector);

            var events = new List<AnomalyEvent>();
            void Feed1(double value)
            {
                AnomalyEvent? closed = detector.Observe(Uniform(value, n), n * 0.01);
                if (closed.HasValue) events.Add(closed.Value);
                n++;
            }

            // A sustained fall that never recovers, well past the cap.
            for (int i = 0; i < 40; i++) Feed1(Normal * 0.80);

            Assert.NotEmpty(events);
            Assert.True(events[0].Truncated);

            // Now a real dropout, deeper than the level the baseline was re-adopted at. Before the
            // cap existed this could not be seen at all: the detector was still inside the first
            // event and emitting nothing.
            int before = events.Count;
            Feed1(Normal * 0.10);
            for (int i = 0; i < 5; i++) Feed1(Normal * 0.80);

            Assert.True(events.Count > before,
                "a dropout after a capped event must still be reported");
            AnomalyEvent found = events[events.Count - 1];
            Assert.False(found.Truncated);
            Assert.True(found.MaxDeviation > 0.5, $"depth was {found.MaxDeviation}");
        }

        [Fact]
        public void TheBaselineIsReadoptedAtTheCapSoDepthReturnsToZero()
        {
            var detector = new AnomalyDetector(Capped(6));
            long n = Warm(detector);

            for (int i = 0; i < 6; i++, n++)
                detector.Observe(Uniform(Normal * 0.80, n), n * 0.01);

            // The cap fired on the sixth frame and took the current level as the new normal.
            Assert.Equal(Normal * 0.80, detector.Baseline, 3);

            detector.Observe(Uniform(Normal * 0.80, n), n * 0.01);
            Assert.Equal(0.0, detector.LastDepth, 6);
        }

        /// <summary>Thresholds with a short onset budget, for the sweep rules.</summary>
        static AnomalyThresholds Sweep(int maxSpread, int minTiles = 8) => new AnomalyThresholds
        {
            Depth = 0.10,
            Coherence = 0.80,
            DebounceFrames = 3,
            BaselineWindow = 9,
            BaselineWarmupFrames = 5,
            MaxEventFrames = 10000,
            MaxOnsetSpreadFrames = maxSpread,
            MinOnsetTiles = minTiles,
        };

        /// <summary>A grid where the first <paramref name="dark"/> tiles have fallen and the rest have not.</summary>
        static TileGrid PartlyDark(int dark, long frame, double normal = Normal, double fallen = 30)
        {
            var g = new TileGrid { FrameNumber = frame };
            for (int i = 0; i < TileGrid.TileCount; i++)
            {
                double v = i < dark ? fallen : normal;
                g.Accumulate(i, (long)v, (long)(v * v), 1);
            }
            return g;
        }

        [Fact]
        public void AFallThatCoveredEveryTileAtOnceIsReported()
        {
            var detector = new AnomalyDetector(Sweep(maxSpread: 12));
            long n = Warm(detector);

            List<AnomalyEvent> events = Feed(detector, n, Normal * 0.2, Normal, Normal, Normal, Normal);

            AnomalyEvent one = Assert.Single(events);
            Assert.Equal(0, one.OnsetSpreadFrames);
            Assert.Equal(TileGrid.TileCount, one.OnsetTiles);
            Assert.Equal(0, detector.EventsRejectedForSpread);
        }

        /// <summary>
        /// The measured false positive this gate exists for. An object crossing the field darkens
        /// the tiles it has reached, and those all move the same way, so depth and coherence read
        /// the same as a panel switching off - 0.91 to 1.00 on the real windows. Sixty windows from
        /// one run split into 1 frame of spread for the clip's full-field dips and 37 to 412 frames
        /// for its moving content, with nothing in between.
        /// </summary>
        [Fact]
        public void AFallThatSweptAcrossTheTilesIsTurnedAway()
        {
            var detector = new AnomalyDetector(Sweep(maxSpread: 12));
            long n = Warm(detector);

            var events = new List<AnomalyEvent>();
            void Feed1(TileGrid g)
            {
                AnomalyEvent? closed = detector.Observe(g, g.FrameNumber * 0.01);
                if (closed.HasValue) events.Add(closed.Value);
            }

            // Two tiles a frame: 64 tiles over 32 frames. The event only opens when the median
            // crosses, around tile 33, so the spread this gate can see is the second half -- about
            // 16 frames, past the 6-frame budget.
            for (int step = 1; step <= 32; step++, n++)
                Feed1(PartlyDark(step * 2, n));

            // Recover, so the event closes.
            for (int i = 0; i < 5; i++, n++)
                Feed1(Uniform(Normal, n));

            Assert.Empty(events);
            Assert.Equal(1, detector.EventsRejectedForSpread);

            AnomalyEvent turned = detector.LastRejectedEvent.Value;
            Assert.True(turned.OnsetSpreadFrames > 6, $"spread was {turned.OnsetSpreadFrames}");
            Assert.True(turned.MaxCoherence > 0.9,
                "the point of the gate: coherence could not tell this from a real dimming, " +
                $"and read {turned.MaxCoherence:F2}");
        }

        [Fact]
        public void ASweepInsideTheBudgetIsStillReported()
        {
            var detector = new AnomalyDetector(Sweep(maxSpread: 6));
            long n = Warm(detector);

            var events = new List<AnomalyEvent>();
            for (int step = 1; step <= 8; step++, n++)   // 64 tiles over 8 frames
            {
                AnomalyEvent? closed = detector.Observe(PartlyDark(step * 8, n), n * 0.01);
                if (closed.HasValue) events.Add(closed.Value);
            }
            for (int i = 0; i < 5; i++, n++)
            {
                AnomalyEvent? closed = detector.Observe(Uniform(Normal, n), n * 0.01);
                if (closed.HasValue) events.Add(closed.Value);
            }

            AnomalyEvent one = Assert.Single(events);
            Assert.True(one.OnsetSpreadFrames <= 6, $"spread was {one.OnsetSpreadFrames}");
            Assert.Equal(0, detector.EventsRejectedForSpread);
        }

        [Fact]
        public void TheGateStandsAsideWhenTooFewTilesFell()
        {
            // A spread measured from a handful of tiles is not a measurement. Rejecting on it would
            // throw away a real fault that happened to cover part of the screen.
            // Most tiles are already down when the median crosses, so few of them get an onset
            // here at all - which is the situation the guard is for.
            // 56 rather than 40: the median crosses around tile 32, so about 40 tiles get an
            // onset here and a guard set at 40 would sit exactly on the boundary.
            var detector = new AnomalyDetector(Sweep(maxSpread: 1, minTiles: 56));
            long n = Warm(detector);

            var events = new List<AnomalyEvent>();
            for (int step = 1; step <= 8; step++, n++)   // 64 tiles over 8 frames, 8 at a time
            {
                AnomalyEvent? closed = detector.Observe(PartlyDark(step * 8, n), n * 0.01);
                if (closed.HasValue) events.Add(closed.Value);
            }
            for (int i = 0; i < 5; i++, n++)
            {
                AnomalyEvent? closed = detector.Observe(Uniform(Normal, n), n * 0.01);
                if (closed.HasValue) events.Add(closed.Value);
            }

            Assert.Single(events);
            Assert.Equal(0, detector.EventsRejectedForSpread);
        }

        [Fact]
        public void ATruncatedEventIsJudgedOnItsSpreadToo()
        {
            // The cap closes an event without the picture recovering, and that path has to go
            // through the same gate - otherwise a sweep long enough to hit the cap gets reported.
            var thresholds = Sweep(maxSpread: 4);
            thresholds.MaxEventFrames = 12;          // fires while the sweep is still spreading
            var detector = new AnomalyDetector(thresholds);
            long n = Warm(detector);

            var events = new List<AnomalyEvent>();
            for (int step = 1; step <= 32; step++, n++)   // two tiles a frame
            {
                AnomalyEvent? closed = detector.Observe(PartlyDark(Math.Min(64, step * 2), n), n * 0.01);
                if (closed.HasValue) events.Add(closed.Value);
            }

            Assert.Empty(events);
            Assert.Equal(1, detector.EventsRejectedForSpread);
            Assert.True(detector.LastRejectedEvent.Value.Truncated);
        }
}
}
