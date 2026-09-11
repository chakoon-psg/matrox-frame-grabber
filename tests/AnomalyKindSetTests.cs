using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// Which kinds the rig watches for. App-wide, and stored by name.
    ///
    /// The flag used to sit on each channel's KindSettings, which made it possible for camera 1 to
    /// watch for Blackout while camera 2 did not - a rig whose report cannot be read. Moving it
    /// needed a migration, and these hold both halves: that an old file keeps what it had, and that
    /// a missing or unreadable list falls back to watching rather than to silence.
    /// </summary>
    public class AnomalyKindSetTests
    {
        [Fact]
        public void The_default_watches_the_only_kind_with_a_detector()
        {
            bool[] on = AnomalyKindSet.Default();

            Assert.Equal(new[] { "Dropout" }, AnomalyKindSet.ToNames(on));
            foreach (AnomalyKind k in AnomalyCatalog.All)
                if (k != AnomalyKind.Dropout)
                    Assert.False(AnomalyKindSet.Get(on, k), k.ToString());
        }

        [Fact]
        public void Names_round_trip()
        {
            bool[] on = AnomalyKindSet.FromNames(new[] { "Dropout", "Blackout" });

            Assert.True(AnomalyKindSet.Get(on, AnomalyKind.Dropout));
            Assert.True(AnomalyKindSet.Get(on, AnomalyKind.Blackout));
            Assert.False(AnomalyKindSet.Get(on, AnomalyKind.Washout));
            Assert.Equal(new[] { "Dropout", "Blackout" }, AnomalyKindSet.ToNames(on));
        }

        /// <summary>
        /// Names, not indices, so appending a kind cannot shift what an older file meant and a kind
        /// from a newer build is ignored rather than switching on a detector that is not here.
        /// </summary>
        [Fact]
        public void An_unknown_name_is_ignored_rather_than_mapped_to_something()
        {
            bool[] on = AnomalyKindSet.FromNames(new[] { "Dropout", "Tearing", "", null });

            Assert.True(AnomalyKindSet.Get(on, AnomalyKind.Dropout));
            Assert.Equal(new[] { "Dropout" }, AnomalyKindSet.ToNames(on));
        }

        [Fact]
        public void Case_and_whitespace_do_not_decide_whether_a_kind_is_watched()
        {
            bool[] on = AnomalyKindSet.FromNames(new[] { " dropout ", "FLICKER" });

            Assert.True(AnomalyKindSet.Get(on, AnomalyKind.Dropout));
            Assert.True(AnomalyKindSet.Get(on, AnomalyKind.Flicker));
        }

        /// <summary>
        /// A lost key must not stop the rig watching: a file that never had the list, or could not
        /// be read, takes the default.
        /// </summary>
        [Fact]
        public void A_missing_list_falls_back_to_watching_not_to_silence()
        {
            Assert.True(AnomalyKindSet.Get(AnomalyKindSet.FromNames(null), AnomalyKind.Dropout));
        }

        /// <summary>
        /// And a list that is there and empty means exactly that. Measured doing the wrong thing:
        /// treating [] as the default made unticking every box revert itself on the next start,
        /// with the log reporting that Dropout was being watched.
        /// </summary>
        [Fact]
        public void An_empty_list_means_nothing_is_watched_not_the_default()
        {
            bool[] on = AnomalyKindSet.FromNames(new string[0]);

            Assert.False(AnomalyKindSet.Get(on, AnomalyKind.Dropout));
            Assert.Empty(AnomalyKindSet.ToNames(on));
        }

        /// <summary>
        /// A list of names this build does not know is taken at its word too. It cannot watch for a
        /// kind it has never heard of, and saying so beats quietly substituting Dropout.
        /// </summary>
        [Fact]
        public void A_list_of_unknown_names_watches_nothing_rather_than_guessing()
        {
            Assert.Empty(AnomalyKindSet.ToNames(AnomalyKindSet.FromNames(new[] { "Tearing" })));
        }

        [Fact]
        public void A_set_of_the_wrong_length_reads_as_off_rather_than_throwing()
        {
            Assert.False(AnomalyKindSet.Get(null, AnomalyKind.Dropout));
            Assert.False(AnomalyKindSet.Get(new bool[2], AnomalyKind.Dropout));
        }

        // ----- the migration -----

        /// <summary>
        /// The union, not the intersection: a kind somebody switched on for one camera is a kind
        /// they meant to be watching for, and taking the intersection would quietly stop watching
        /// for it everywhere.
        /// </summary>
        [Fact]
        public void The_flag_moved_out_of_the_channels_as_their_union()
        {
            var a = new DetectionSettings();
            var b = new DetectionSettings();
            a.For(AnomalyKind.Dropout).Enabled = true;
            b.For(AnomalyKind.Dropout).Enabled = true;
            b.For(AnomalyKind.Washout).Enabled = true;   // switched on for one camera only

            bool[] on = AnomalyKindSet.UnionOf(new[] { a, b });

            Assert.True(AnomalyKindSet.Get(on, AnomalyKind.Dropout));
            Assert.True(AnomalyKindSet.Get(on, AnomalyKind.Washout));
            Assert.False(AnomalyKindSet.Get(on, AnomalyKind.Blackout));
        }

        /// <summary>
        /// The flag the union actually reads is the one an old file put in StoredEnabled, not the
        /// live copy. Without that the migration is blind: the change that moved the flag also
        /// stopped deserializing it, so every channel would read as off and the intent would be
        /// replaced by the fallback.
        /// </summary>
        [Fact]
        public void The_union_reads_the_flag_as_the_old_file_stored_it()
        {
            var loaded = new DetectionSettings();
            foreach (AnomalyKind k in AnomalyCatalog.All) loaded.For(k).Enabled = false;
            loaded.For(AnomalyKind.Dropout).StoredEnabled = true;
            loaded.For(AnomalyKind.Blackout).StoredEnabled = true;
            loaded.For(AnomalyKind.Washout).StoredEnabled = false;

            bool[] on = AnomalyKindSet.UnionOf(new[] { loaded });

            Assert.True(AnomalyKindSet.Get(on, AnomalyKind.Dropout));
            Assert.True(AnomalyKindSet.Get(on, AnomalyKind.Blackout));
            Assert.False(AnomalyKindSet.Get(on, AnomalyKind.Washout));
        }

        /// <summary>
        /// And it must not travel any further than the migration. CopyFrom carries the live flag
        /// but deliberately not the stored one: if it did, "Enabled" would be written back into
        /// every channel on the next save and the file would hold four answers beside the one -
        /// which is the state this whole change exists to remove.
        /// </summary>
        [Fact]
        public void The_old_files_key_does_not_travel_onto_the_live_settings()
        {
            var loaded = new DetectionSettings();
            loaded.For(AnomalyKind.Dropout).StoredEnabled = true;

            var live = new DetectionSettings();
            live.CopyFrom(loaded);

            Assert.Null(live.For(AnomalyKind.Dropout).StoredEnabled);
        }

        [Fact]
        public void A_migration_from_channels_that_watched_nothing_still_watches_dropout()
        {
            var a = new DetectionSettings();
            foreach (AnomalyKind k in AnomalyCatalog.All) a.For(k).Enabled = false;

            Assert.True(AnomalyKindSet.Get(AnomalyKindSet.UnionOf(new[] { a }), AnomalyKind.Dropout));
            Assert.True(AnomalyKindSet.Get(AnomalyKindSet.UnionOf(null), AnomalyKind.Dropout));
        }

        // ----- what it says -----

        /// <summary>
        /// Both halves of the line matter. Naming only what is on would let a rig with nothing to
        /// report read as "nothing wrong" while six of seven kinds go unexamined.
        /// </summary>
        [Fact]
        public void The_line_names_what_is_on_and_counts_what_is_not()
        {
            string s = AnomalyKindSet.Describe(AnomalyKindSet.Default());

            Assert.StartsWith("Dropout", s);
            Assert.Contains((AnomalyCatalog.Count - 1) + " off", s);
        }

        [Fact]
        public void A_kind_that_is_on_without_a_detector_is_marked_as_such()
        {
            bool[] on = AnomalyKindSet.FromNames(new[] { "Dropout", "Flip" });

            Assert.Contains("Flip (미구현)", AnomalyKindSet.Describe(on));
        }

        [Fact]
        public void Watching_nothing_says_so_in_words()
        {
            string s = AnomalyKindSet.Describe(new bool[AnomalyCatalog.Count]);

            Assert.Contains("none", s);
            Assert.Contains(AnomalyCatalog.Count.ToString(), s);
        }
    }
}
