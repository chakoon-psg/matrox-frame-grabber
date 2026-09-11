using System;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The per-kind detection settings: how stored intent becomes the frame counts the detector
    /// works in, how the false-positive budget is shared, and what happens to a settings file
    /// written by the build before this one.
    /// </summary>
    public class DetectionSettingsTests
    {
        const double Rate = DetectionSettings.LegacyFrameRate;   // 124.316

        // ----- milliseconds to frames -----

        /// <summary>
        /// The values in service were authored as frame counts at 124.316 fps, so resolving the
        /// stored milliseconds at that rate has to give them back exactly - otherwise the migration
        /// changes the detector's behaviour while claiming not to.
        /// </summary>
        [Fact]
        public void Resolving_at_the_rate_the_values_were_authored_at_gives_the_original_frames()
        {
            AnomalyThresholds t = new DetectionSettings().Resolve(AnomalyKind.Dropout, Rate);

            Assert.Equal(20, t.DebounceFrames);
            Assert.Equal(250, t.MaxEventFrames);
            Assert.Equal(6, t.MaxOnsetSpreadFrames);
            Assert.Equal(91, t.BaselineWindow);
            Assert.Equal(30, t.BaselineWarmupFrames);
        }

        /// <summary>
        /// And the whole reason for storing time: the same setting is a different number of frames
        /// on a slower camera. A debounce of 161 ms is 20 frames here and 5 on a 30 fps webcam -
        /// where the old frame-based 20 would have meant 667 ms.
        /// </summary>
        [Fact]
        public void The_same_setting_resolves_to_fewer_frames_on_a_slower_camera()
        {
            var s = new DetectionSettings();
            Assert.Equal(20, s.Resolve(AnomalyKind.Dropout, 124.316).DebounceFrames);
            Assert.Equal(5, s.Resolve(AnomalyKind.Dropout, 30.0).DebounceFrames);
        }

        /// <summary>
        /// A duration shorter than a frame period still means one frame. Zero would turn the
        /// detector off in a way that looks like a quiet rig.
        /// </summary>
        [Fact]
        public void A_duration_shorter_than_a_frame_still_resolves_to_one_frame()
        {
            var s = new DetectionSettings();
            s.For(AnomalyKind.Dropout).DebounceMs = 1.0;
            Assert.Equal(1, s.Resolve(AnomalyKind.Dropout, 30.0).DebounceFrames);
        }

        [Fact]
        public void Resolving_carries_the_deviation_and_the_provenance_across()
        {
            var s = new DetectionSettings();
            s.ApplyCalibration(AnomalyKind.Dropout, 0.15, "2026-09-08T12:00:00", 223800, 0.0475);

            AnomalyThresholds t = s.Resolve(AnomalyKind.Dropout, Rate);
            Assert.Equal(0.15, t.Depth, 6);
            Assert.Equal("2026-09-08T12:00:00", t.CalibratedAt);
            Assert.Equal(223800, t.CalibrationFrames);
            Assert.Equal(0.0475, t.CalibrationFloor, 6);
        }

        // ----- the budget split -----

        /// <summary>
        /// The rule that matters and could not otherwise be checked yet: only one kind is
        /// implemented, so the live divisor can never exceed one until a second detector exists.
        /// </summary>
        [Fact]
        public void The_budget_is_divided_among_the_running_detectors()
        {
            Assert.Equal(1.0, DetectionSettings.BudgetShare(1.0, 1), 6);
            Assert.Equal(0.5, DetectionSettings.BudgetShare(1.0, 2), 6);
            Assert.Equal(0.2, DetectionSettings.BudgetShare(1.0, 5), 6);
        }

        /// <summary>Nothing enabled must not divide by zero.</summary>
        [Fact]
        public void The_whole_budget_goes_to_a_single_detector_or_to_none()
        {
            Assert.Equal(1.0, DetectionSettings.BudgetShare(1.0, 0), 6);
        }

        [Fact]
        public void The_resolved_thresholds_carry_the_share_not_the_total()
        {
            var s = new DetectionSettings { FalsePositiveBudgetPerHour = 2.0 };
            Assert.Equal(s.BudgetPerEnabledKind,
                         s.Resolve(AnomalyKind.Dropout, Rate).FalsePositiveBudgetPerHour, 6);
        }

        // ----- defaults -----

        /// <summary>
        /// Only the kind that has been measured is on. A detector switching itself on after an
        /// update, uncalibrated, would look like the panel had started failing.
        /// </summary>
        [Fact]
        public void Only_dropout_is_enabled_by_default()
        {
            var s = new DetectionSettings();
            Assert.True(s.For(AnomalyKind.Dropout).Enabled);
            foreach (AnomalyKind k in AnomalyCatalog.All)
                if (k != AnomalyKind.Dropout)
                    Assert.False(s.For(k).Enabled, $"{k} should be off");
        }

        [Fact]
        public void Only_dropout_counts_as_running_even_if_another_kind_is_switched_on()
        {
            var s = new DetectionSettings();
            s.For(AnomalyKind.Washout).Enabled = true;   // no detector for it yet
            Assert.Equal(1, s.EnabledCount);
        }

        // ----- migration -----

        /// <summary>
        /// The value that was actually tuned - 0.05 on two channels and 0.15 on the dim one - has
        /// to survive exactly. Losing it puts every channel back on a default with nothing on
        /// screen to say so: the detector keeps running and only the numbers change.
        /// </summary>
        [Fact]
        public void Migrating_keeps_the_hand_tuned_depth_exactly()
        {
            var legacy = new AnomalyThresholds { Depth = 0.15, Coherence = 0.8 };
            DetectionSettings s = DetectionSettings.FromLegacy(legacy);

            Assert.Equal(0.15, s.For(AnomalyKind.Dropout).Deviation, 6);
            Assert.Equal(0.8, s.For(AnomalyKind.Dropout).Coherence, 6);
        }

        /// <summary>
        /// And the frame counts have to come back as themselves when resolved at the rate they were
        /// authored at, or the migration quietly retunes the detector.
        /// </summary>
        [Fact]
        public void Migrating_round_trips_the_frame_counts_at_the_legacy_rate()
        {
            var legacy = new AnomalyThresholds();   // the defaults that were in service
            AnomalyThresholds t = DetectionSettings.FromLegacy(legacy).Resolve(AnomalyKind.Dropout, Rate);

            Assert.Equal(legacy.DebounceFrames, t.DebounceFrames);
            Assert.Equal(legacy.MaxEventFrames, t.MaxEventFrames);
            Assert.Equal(legacy.MaxOnsetSpreadFrames, t.MaxOnsetSpreadFrames);
            Assert.Equal(legacy.BaselineWindow, t.BaselineWindow);
            Assert.Equal(legacy.BaselineWarmupFrames, t.BaselineWarmupFrames);
            Assert.Equal(legacy.MinOnsetTiles, t.MinOnsetTiles);
        }

        [Fact]
        public void Migrating_enables_dropout_and_leaves_the_new_kinds_off()
        {
            DetectionSettings s = DetectionSettings.FromLegacy(new AnomalyThresholds());
            Assert.True(s.For(AnomalyKind.Dropout).Enabled);
            Assert.False(s.For(AnomalyKind.Blackout).Enabled);
            Assert.False(s.For(AnomalyKind.Flip).Enabled);
        }

        [Fact]
        public void Migrating_keeps_the_calibration_provenance()
        {
            var legacy = new AnomalyThresholds
            {
                CalibratedAt = "2026-09-08T12:00:00",
                CalibrationFrames = 223800,
                CalibrationFloor = 0.0475,
            };
            KindSettings k = DetectionSettings.FromLegacy(legacy).For(AnomalyKind.Dropout);

            Assert.Equal("2026-09-08T12:00:00", k.CalibratedAt);
            Assert.Equal(223800, k.CalibrationFrames);
            Assert.Equal(0.0475, k.CalibrationFloor, 6);
        }

        // ----- clamping, so what is stored is what is shown -----

        [Fact]
        public void A_typed_zero_deviation_is_clamped_where_it_is_stored()
        {
            var s = new DetectionSettings();
            KindSettings k = s.For(AnomalyKind.Dropout);
            k.Deviation = 0.0;
            k.Coherence = 5.0;
            k.MinOnsetTiles = 999;
            k.Clamp();

            Assert.Equal(0.001, k.Deviation, 6);
            Assert.Equal(1.0, k.Coherence, 6);
            Assert.Equal(TileGrid.TileCount, k.MinOnsetTiles);
        }

        [Fact]
        public void A_hand_edited_file_that_lost_the_per_kind_array_is_repaired()
        {
            var s = new DetectionSettings { PerKind = null };
            Assert.NotNull(s.For(AnomalyKind.Flicker));
            Assert.Equal(AnomalyCatalog.Count, s.PerKind.Length);

            s.PerKind = new KindSettings[2];   // too short, as a truncated file would give
            Assert.NotNull(s.For(AnomalyKind.Flip));
            Assert.Equal(AnomalyCatalog.Count, s.PerKind.Length);
        }

        /// <summary>
        /// The migration that adding a kind needs, and the reason this is not just repair: a
        /// settings file written when there were five kinds is *not* corrupt, it is one version old,
        /// and the array used to be thrown away whenever its length disagreed. That would have
        /// dropped a depth measured over 8713 frames back to the default while the detector kept
        /// running - a change with nothing on screen to announce it.
        /// </summary>
        [Fact]
        public void A_file_written_before_a_kind_was_added_keeps_what_it_had()
        {
            var s = new DetectionSettings();
            var five = new KindSettings[5];
            for (int i = 0; i < five.Length; i++) five[i] = new KindSettings();
            five[0].Enabled = true;
            five[0].Deviation = 0.15;               // the dim channel's measured depth
            five[0].CalibratedAt = "2026-09-10";
            five[0].CalibrationFrames = 8713;
            five[2].Enabled = true;                 // and someone had switched Washout on
            s.PerKind = five;

            KindSettings dropout = s.For(AnomalyKind.Dropout);

            Assert.Equal(AnomalyCatalog.Count, s.PerKind.Length);
            Assert.Equal(0.15, dropout.Deviation, 6);
            Assert.Equal("2026-09-10", dropout.CalibratedAt);
            Assert.Equal(8713, dropout.CalibrationFrames);
            Assert.True(s.For(AnomalyKind.Washout).Enabled);

            // And the appended kinds arrive off, so an uncalibrated detector cannot switch itself
            // on behind an update.
            Assert.False(s.For(AnomalyKind.Freeze).Enabled);
            Assert.False(s.For(AnomalyKind.ColorShift).Enabled);
        }

        [Fact]
        public void A_file_from_a_later_version_is_truncated_rather_than_refused()
        {
            var s = new DetectionSettings();
            var many = new KindSettings[AnomalyCatalog.Count + 3];
            for (int i = 0; i < many.Length; i++) many[i] = new KindSettings();
            many[0].Deviation = 0.2;
            s.PerKind = many;

            Assert.Equal(0.2, s.For(AnomalyKind.Dropout).Deviation, 6);
            Assert.Equal(AnomalyCatalog.Count, s.PerKind.Length);
        }

        [Fact]
        public void CopyFrom_carries_every_kind_and_the_shared_values()
        {
            var a = new DetectionSettings { FalsePositiveBudgetPerHour = 3.0, BaselineWindowMs = 500.0 };
            a.For(AnomalyKind.Dropout).Deviation = 0.2;
            a.For(AnomalyKind.Washout).Enabled = true;

            var b = new DetectionSettings();
            b.CopyFrom(a);

            Assert.Equal(3.0, b.FalsePositiveBudgetPerHour, 6);
            Assert.Equal(500.0, b.BaselineWindowMs, 6);
            Assert.Equal(0.2, b.For(AnomalyKind.Dropout).Deviation, 6);
            Assert.True(b.For(AnomalyKind.Washout).Enabled);
        }
    }

    /// <summary>What is known about each kind without asking a detector.</summary>
    public class AnomalyCatalogTests
    {
        /// <summary>
        /// Two kinds have a detector, and they are different kinds of detector. Dropout measures
        /// a fall relative to a running baseline and confirms on one frame; Blackout measures an
        /// absolute level with hysteresis and must not. The rest have none yet, and the settings
        /// window says so rather than hiding them.
        /// </summary>
        [Fact]
        public void Two_kinds_have_a_detector_and_the_rest_say_so()
        {
            Assert.True(AnomalyCatalog.Implemented(AnomalyKind.Dropout));
            Assert.True(AnomalyCatalog.Implemented(AnomalyKind.Blackout));

            foreach (AnomalyKind k in AnomalyCatalog.All)
                if (k != AnomalyKind.Dropout && k != AnomalyKind.Blackout)
                    Assert.False(AnomalyCatalog.Implemented(k), $"{k} has no detector yet");
        }

        /// <summary>
        /// Which of the two judges it. The caller needs this because they take different settings
        /// and emit on different occasions - one on close, one on confirm.
        /// </summary>
        [Fact]
        public void Only_blackout_is_judged_by_the_sustained_detector()
        {
            Assert.True(AnomalyCatalog.IsSustained(AnomalyKind.Blackout));
            Assert.False(AnomalyCatalog.IsSustained(AnomalyKind.Dropout));
        }

        /// <summary>
        /// Blackout's knobs are luma, and Dropout's are a fraction. Reading one as the other is a
        /// 0.05-against-255 mistake that would silence or saturate the detector, so the defaults
        /// are checked to be in the right units.
        /// </summary>
        [Fact]
        public void Blackout_resolves_absolute_levels_and_a_hysteresis()
        {
            var s = new DetectionSettings();

            BlackoutThresholds t = s.ResolveBlackout(AnomalyKind.Blackout);

            Assert.InRange(t.EnterLuma, 1.0, 254.0);      // luma, not a fraction
            Assert.True(t.ExitLuma > t.EnterLuma, "the pair has to be a hysteresis");
            Assert.True(t.MaxSpread > 0.0);
            // Its own field, not DebounceMs. Sharing that one meant sharing its default, which
            // is Dropout's calibrated 161 ms - a third of what a sustained kind should wait.
            Assert.Equal(s.For(AnomalyKind.Blackout).BlackoutEnterMs, t.EnterMs, 3);
            Assert.True(t.EnterMs >= 500.0, "a sustained kind waits half a second, not 161 ms");
        }

        [Fact]
        public void Every_kind_is_listed_so_the_settings_window_can_show_what_is_not_checked()
        {
            Assert.Equal(AnomalyCatalog.Count, AnomalyCatalog.All.Length);
            foreach (AnomalyKind k in (AnomalyKind[])Enum.GetValues(typeof(AnomalyKind)))
                Assert.Contains(k, AnomalyCatalog.All);
        }

        /// <summary>
        /// Every kind has to describe itself, because the description is the tooltip on a checkbox
        /// nobody can tick - it is the only thing on screen that says what is not being watched.
        /// </summary>
        [Fact]
        public void Every_kind_says_what_it_looks_like()
        {
            foreach (AnomalyKind k in AnomalyCatalog.All)
                Assert.False(string.IsNullOrWhiteSpace(AnomalyCatalog.Describe(k)), k.ToString());
        }

        /// <summary>
        /// Freeze compares the other way round, and this is where that is written down: its number
        /// is how much change still counts as no change. A detector built to the shape of the other
        /// six would report a fault whenever the picture was alive.
        /// </summary>
        [Fact]
        public void Freeze_is_the_one_kind_whose_threshold_is_a_ceiling()
        {
            Assert.True(AnomalyCatalog.DeviationIsCeiling(AnomalyKind.Freeze));
            foreach (AnomalyKind k in AnomalyCatalog.All)
                if (k != AnomalyKind.Freeze)
                    Assert.False(AnomalyCatalog.DeviationIsCeiling(k), k.ToString());

            Assert.Equal("delta", AnomalyCatalog.DeviationWord(AnomalyKind.Freeze));
        }

        /// <summary>
        /// Appended, never renumbered: the numbers are the settings file's keys, so changing one
        /// would hand a channel another kind's threshold.
        /// </summary>
        [Fact]
        public void The_kind_numbers_are_the_settings_files_keys()
        {
            Assert.Equal(0, (int)AnomalyKind.Dropout);
            Assert.Equal(1, (int)AnomalyKind.Blackout);
            Assert.Equal(2, (int)AnomalyKind.Washout);
            Assert.Equal(3, (int)AnomalyKind.Flicker);
            Assert.Equal(4, (int)AnomalyKind.Flip);
            Assert.Equal(5, (int)AnomalyKind.Freeze);
            Assert.Equal(6, (int)AnomalyKind.ColorShift);
        }

        /// <summary>
        /// A rise is not a depth. Printing "depth 0.12" for something that got brighter is a lie
        /// about what was measured.
        /// </summary>
        [Fact]
        public void A_rising_kind_is_not_described_with_a_depth()
        {
            Assert.Equal(AnomalyDirection.Fall, AnomalyCatalog.DirectionOf(AnomalyKind.Dropout));
            Assert.Equal(AnomalyDirection.Fall, AnomalyCatalog.DirectionOf(AnomalyKind.Blackout));
            Assert.Equal(AnomalyDirection.Rise, AnomalyCatalog.DirectionOf(AnomalyKind.Washout));
            Assert.Equal(AnomalyDirection.Either, AnomalyCatalog.DirectionOf(AnomalyKind.Flicker));

            Assert.Equal("depth", AnomalyCatalog.DeviationWord(AnomalyKind.Dropout));
            Assert.Equal("rise", AnomalyCatalog.DeviationWord(AnomalyKind.Washout));
            Assert.Equal("dev", AnomalyCatalog.DeviationWord(AnomalyKind.Flip));
        }

        [Fact]
        public void An_out_of_range_kind_indexes_safely_rather_than_throwing()
        {
            Assert.Equal(0, AnomalyCatalog.Index((AnomalyKind)99));
            Assert.Equal(0, AnomalyCatalog.Index((AnomalyKind)(-1)));
        }
    }

    /// <summary>An event says which kind it is, and describes itself in that kind's words.</summary>
    public class AnomalyEventKindTests
    {
        static AnomalyEvent Make(AnomalyKind kind) =>
            new AnomalyEvent(100, 107, 7, 0.12, 0.93, 12.5, 56.3,
                             truncated: false, onsetSpreadFrames: 5, onsetTiles: 10, kind: kind);

        [Fact]
        public void An_event_defaults_to_the_only_implemented_kind()
        {
            var e = new AnomalyEvent(1, 2, 2, 0.1, 0.9, 0.0, 16.0);
            Assert.Equal(AnomalyKind.Dropout, e.Kind);
            Assert.Equal(AnomalyDirection.Fall, e.Direction);
        }

        [Fact]
        public void The_report_names_the_kind()
        {
            Assert.StartsWith("Dropout frame 100-107", Make(AnomalyKind.Dropout).ToString());
            Assert.StartsWith("Washout frame 100-107", Make(AnomalyKind.Washout).ToString());
        }

        [Fact]
        public void The_report_uses_the_word_that_fits_the_direction()
        {
            Assert.Contains("depth 0.12", Make(AnomalyKind.Dropout).ToString());
            Assert.Contains("rise 0.12", Make(AnomalyKind.Washout).ToString());
            Assert.DoesNotContain("depth", Make(AnomalyKind.Washout).ToString());
        }

        [Fact]
        public void A_truncated_event_still_says_its_duration_is_a_floor()
        {
            var e = new AnomalyEvent(1, 250, 250, 0.9, 1.0, 0.0, 2010.0, truncated: true);
            Assert.Contains("still running", e.ToString());
        }
    }
}
