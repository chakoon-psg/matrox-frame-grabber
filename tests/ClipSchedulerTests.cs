using System.Collections.Generic;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// Which confirmed events become clips. These exist because the detector closes an event at the
    /// duration cap - 2010 ms - so a fault that stays produces one every two seconds, and a clip
    /// each would write the same ten seconds thirty times over a minute.
    /// </summary>
    public class ClipSchedulerTests
    {
        static readonly AnomalyClipSettings Around5 = new AnomalyClipSettings(5.0, 2.0);

        /// <summary>An event of <paramref name="kind"/> starting at <paramref name="startSec"/>.</summary>
        static AnomalyEvent Ev(AnomalyKind kind, double startSec, double durMs = 100.0,
                               bool truncated = false, double dev = 0.2, long frame = 0)
        {
            long f = frame > 0 ? frame : (long)(startSec * 124.316);
            return new AnomalyEvent(f, f + 10, 10, dev, 0.95, startSec, durMs,
                                    truncated: truncated, kind: kind);
        }

        static List<ClipRequest> Drain(ClipScheduler s, double nowSec)
        {
            var got = new List<ClipRequest>();
            while (s.TryTakeDue(nowSec, out ClipRequest r)) got.Add(r);
            return got;
        }

        // ----- the window -----

        [Fact]
        public void A_clip_covers_the_configured_seconds_either_side_of_the_event()
        {
            var s = new ClipScheduler(Around5);
            s.Offer(Ev(AnomalyKind.Dropout, 100.0, durMs: 2000.0));

            List<ClipRequest> got = Drain(s, 200.0);
            ClipRequest r = Assert.Single(got);
            Assert.Equal(95.0, r.FromSec, 3);
            Assert.Equal(107.0, r.ToSec, 3);   // 100 + 2 s event + 5 s after
        }

        /// <summary>
        /// The window cannot be cut when the event is reported: the "after" seconds have not
        /// happened, and the files holding them are still open.
        /// </summary>
        [Fact]
        public void A_clip_is_not_due_until_after_the_window_and_the_segment_close()
        {
            var s = new ClipScheduler(Around5);
            s.Offer(Ev(AnomalyKind.Dropout, 100.0, durMs: 2000.0));

            Assert.Empty(Drain(s, 107.0));            // window just ended, file still open
            Assert.Empty(Drain(s, 108.9));
            Assert.Single(Drain(s, 109.0));           // 107 + 2 s
        }

        [Fact]
        public void A_window_never_starts_before_the_beginning_of_the_run()
        {
            var s = new ClipScheduler(Around5);
            s.Offer(Ev(AnomalyKind.Dropout, 1.0));
            ClipRequest r = Assert.Single(Drain(s, 100.0));
            Assert.Equal(0.0, r.FromSec, 6);
        }

        // ----- the storm this exists to stop -----

        /// <summary>
        /// A blackout lasting a minute closes an event every 2.01 s. One clip, not thirty.
        /// </summary>
        [Fact]
        public void A_sustained_fault_folds_into_one_clip_rather_than_one_per_cap()
        {
            var s = new ClipScheduler(Around5);
            for (int i = 0; i < 30; i++)
                s.Offer(Ev(AnomalyKind.Blackout, 100.0 + i * 2.01, durMs: 2010.0, truncated: true));

            List<ClipRequest> got = Drain(s, 400.0);
            ClipRequest r = Assert.Single(got);
            Assert.Equal(30, r.Occurrences);
            Assert.True(r.Truncated);
            Assert.Equal(29, s.Merged);
            Assert.Equal(1, s.Emitted);
        }

        /// <summary>And the folded clip covers the whole run of it, not just the first two seconds.</summary>
        [Fact]
        public void A_folded_clip_extends_to_cover_every_occurrence()
        {
            var s = new ClipScheduler(Around5);
            s.Offer(Ev(AnomalyKind.Blackout, 100.0, durMs: 2010.0, truncated: true));
            s.Offer(Ev(AnomalyKind.Blackout, 102.01, durMs: 2010.0, truncated: true));

            ClipRequest r = Assert.Single(Drain(s, 400.0));
            Assert.Equal(95.0, r.FromSec, 2);
            Assert.Equal(109.02, r.ToSec, 2);   // 102.01 + 2.01 + 5
        }

        /// <summary>
        /// Once a clip is cut, the same kind waits out its cooldown - a blackout still going an
        /// hour later should not have produced sixty clips.
        /// </summary>
        [Fact]
        public void After_a_clip_the_same_kind_is_held_off_for_its_cooldown()
        {
            var s = new ClipScheduler(Around5);
            s.Offer(Ev(AnomalyKind.Blackout, 100.0, durMs: 100.0));
            Assert.Single(Drain(s, 200.0));

            // Well past the merge gap, well inside the 60 s cooldown.
            Assert.False(s.Offer(Ev(AnomalyKind.Blackout, 130.0)));
            Assert.Equal(1, s.Suppressed);
            Assert.Empty(Drain(s, 300.0));

            // And allowed again once the cooldown has passed.
            Assert.True(s.Offer(Ev(AnomalyKind.Blackout, 200.0)));
            Assert.Single(Drain(s, 300.0));
        }

        /// <summary>
        /// Dropouts are the thing being counted, so every one gets a clip. A cooldown here would
        /// throw away the occurrences the panel is being watched for.
        /// </summary>
        [Fact]
        public void Every_dropout_gets_its_own_clip()
        {
            var s = new ClipScheduler(Around5);
            s.Offer(Ev(AnomalyKind.Dropout, 100.0));
            Assert.Single(Drain(s, 200.0));

            Assert.True(s.Offer(Ev(AnomalyKind.Dropout, 105.0)));
            Assert.Single(Drain(s, 200.0));
            Assert.Equal(0, s.Suppressed);
            Assert.Equal(2, s.Emitted);
        }

        /// <summary>Two dropouts inside the debounce-sized merge gap are one occurrence.</summary>
        [Fact]
        public void Dropouts_closer_than_the_merge_gap_are_one_occurrence()
        {
            var s = new ClipScheduler(Around5);
            s.Offer(Ev(AnomalyKind.Dropout, 100.0, durMs: 100.0));
            s.Offer(Ev(AnomalyKind.Dropout, 100.5, durMs: 100.0));

            ClipRequest r = Assert.Single(Drain(s, 200.0));
            Assert.Equal(2, r.Occurrences);
        }

        [Fact]
        public void Different_kinds_do_not_fold_into_each_other()
        {
            var s = new ClipScheduler(Around5);
            s.Offer(Ev(AnomalyKind.Dropout, 100.0));
            s.Offer(Ev(AnomalyKind.Washout, 100.2));

            Assert.Equal(2, Drain(s, 200.0).Count);
            Assert.Equal(0, s.Merged);
        }

        [Fact]
        public void A_clip_carries_the_deepest_deviation_of_what_it_covers()
        {
            var s = new ClipScheduler(Around5);
            s.Offer(Ev(AnomalyKind.Blackout, 100.0, dev: 0.30));
            s.Offer(Ev(AnomalyKind.Blackout, 101.0, dev: 0.90));
            s.Offer(Ev(AnomalyKind.Blackout, 102.0, dev: 0.55));

            ClipRequest r = Assert.Single(Drain(s, 200.0));
            Assert.Equal(0.90, r.MaxDeviation, 6);
        }

        // ----- bounds -----

        /// <summary>
        /// The point of the class is that a storm makes few files, so it must not instead make a
        /// large amount of state.
        /// </summary>
        [Fact]
        public void Pending_clips_are_bounded()
        {
            var s = new ClipScheduler(new AnomalyClipSettings(0.1, 0.1));
            for (int i = 0; i < 50; i++)
                s.Offer(Ev(AnomalyKind.Dropout, 100.0 + i * 10.0));   // past every merge gap

            Assert.Equal(ClipScheduler.MaxPending, s.PendingCount);
            Assert.True(s.Overflowed > 0);
        }

        // ----- stopping -----

        /// <summary>
        /// A fault still running when the grab ends leaves a clip short of its "after" seconds.
        /// That is the truth about it, and better than discarding the evidence for being
        /// incomplete.
        /// </summary>
        [Fact]
        public void Flush_hands_out_what_is_pending_even_though_it_is_not_due()
        {
            var s = new ClipScheduler(Around5);
            s.Offer(Ev(AnomalyKind.Dropout, 100.0));
            Assert.Empty(Drain(s, 101.0));

            var flushed = new List<ClipRequest>(s.Flush());
            Assert.Single(flushed);
            Assert.Equal(0, s.PendingCount);
        }

        [Fact]
        public void Reset_forgets_the_cooldowns_as_well_as_the_queue()
        {
            var s = new ClipScheduler(Around5);
            s.Offer(Ev(AnomalyKind.Blackout, 100.0));
            Drain(s, 200.0);
            s.Reset();

            Assert.True(s.Offer(Ev(AnomalyKind.Blackout, 130.0)));   // would be suppressed otherwise
            Assert.Equal(0, s.Suppressed);
        }
    }

    /// <summary>The per-kind policy, and the retention it implies.</summary>
    public class AnomalyClipPolicyTests
    {
        [Fact]
        public void A_dropout_is_reported_every_time_and_a_sustained_fault_is_not()
        {
            Assert.Equal(0.0, AnomalyClipPolicy.For(AnomalyKind.Dropout).CooldownSec);
            Assert.True(AnomalyClipPolicy.For(AnomalyKind.Blackout).CooldownSec > 0.0);
            Assert.True(AnomalyClipPolicy.For(AnomalyKind.Washout).CooldownSec > 0.0);
        }

        /// <summary>
        /// A merge gap under the duration cap would leave a sustained fault producing a clip every
        /// 2.01 s, which is the failure this policy exists to prevent.
        /// </summary>
        [Fact]
        public void A_sustained_kinds_merge_gap_clears_the_duration_cap()
        {
            const double capSec = 2.010;
            Assert.True(AnomalyClipPolicy.For(AnomalyKind.Blackout).MergeGapSec > capSec);
            Assert.True(AnomalyClipPolicy.For(AnomalyKind.Washout).MergeGapSec > capSec);
        }

        /// <summary>
        /// Four frames out of an oscillation say nothing the tile history does not say better, and
        /// the tile history already exists.
        /// </summary>
        [Fact]
        public void Flicker_is_evidenced_by_the_waveform_and_not_by_stills()
        {
            AnomalyClipPolicy p = AnomalyClipPolicy.For(AnomalyKind.Flicker);
            Assert.False(p.Stills);
            Assert.True(p.Waveform);
        }

        [Fact]
        public void A_flip_is_evidenced_by_a_single_frame()
        {
            Assert.True(AnomalyClipPolicy.For(AnomalyKind.Flip).Stills);
            Assert.False(AnomalyClipPolicy.For(AnomalyKind.Flip).Waveform);
        }

        /// <summary>
        /// The ring has to still hold the start of a window when the clip is finally cut, and the
        /// settings window shows this rather than letting somebody set retention separately - a
        /// ring shorter than the window makes clips quietly short.
        /// </summary>
        [Fact]
        public void The_required_retention_covers_both_sides_the_event_and_the_close()
        {
            var s = new AnomalyClipSettings(5.0, 2.0);
            Assert.Equal(5.0 + 5.0 + 2.010 + 2.0, s.RequiredRingSec(2.010), 3);
        }

        [Fact]
        public void Negative_settings_are_treated_as_zero_rather_than_inverting_a_window()
        {
            var s = new AnomalyClipSettings(-3.0, -1.0);
            Assert.Equal(0.0, s.AroundSec);
            Assert.Equal(0.0, s.SegmentCloseSec);
        }
    }
}
