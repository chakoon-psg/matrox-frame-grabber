using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// What gives when the local disk runs low.
    ///
    /// These exist because the failure mode is asymmetric. Deleting context video too eagerly
    /// costs a few hours of backdrop; deleting the wrong thing, or refusing to delete until the
    /// disk is full, costs the evidence a reliability test exists to produce - and a full disk
    /// takes the detection with it.
    /// </summary>
    public class StoragePolicyTests
    {
        // The operating point this rig runs at: three channels, decim 2, four stills of
        // 1024x772x3 per event plus a clip, five-minute H.264 segments of a few hundred MB.
        const int Channels = 3;
        const long EvidencePerEvent = 4 * 2371584 + 500_000;   // stills + a clip
        const long SegmentBytes = 300_000_000;

        [Fact]
        public void There_is_always_room_left_for_windows_itself()
        {
            Assert.Equal(StoragePolicy.FloorGb, StoragePolicy.MinimumReserveGb(1, 0, 0), 6);
            Assert.True(StoragePolicy.MinimumReserveGb(Channels, EvidencePerEvent, SegmentBytes)
                        > StoragePolicy.FloorGb);
        }

        /// <summary>
        /// The minimum is what the rig needs at the geometry it is running, not a constant: the
        /// evidence for one event is four stills, and a still at decim 1 is four times the bytes
        /// of one at decim 2.
        /// </summary>
        [Fact]
        public void The_minimum_follows_the_operating_point()
        {
            double decim2 = StoragePolicy.MinimumReserveGb(Channels, EvidencePerEvent, SegmentBytes);
            double decim1 = StoragePolicy.MinimumReserveGb(Channels, EvidencePerEvent * 4, SegmentBytes * 4);

            Assert.True(decim1 > decim2, "a bigger frame needs a bigger reserve");
            Assert.True(decim2 > 3.0 && decim2 < 6.0, $"3 channels at decim 2 wanted {decim2:F2} GB");
        }

        /// <summary>
        /// A reserve below the minimum is worse than none: the disk fills exactly when an anomaly
        /// needs writing, so the one file the rig exists to produce is the one that fails. The
        /// number is clamped rather than refused, because whoever typed it could not have known
        /// the minimum - it depends on the decimation in force.
        /// </summary>
        [Fact]
        public void A_reserve_too_small_to_write_an_event_is_raised_not_obeyed()
        {
            double min = StoragePolicy.MinimumReserveGb(Channels, EvidencePerEvent, SegmentBytes);

            Assert.Equal(min, StoragePolicy.ClampReserveGb(0.0, min), 6);
            Assert.Equal(min, StoragePolicy.ClampReserveGb(1.0, min), 6);
            Assert.Equal(min, StoragePolicy.ClampReserveGb(double.NaN, min), 6);
            Assert.Equal(20.0, StoragePolicy.ClampReserveGb(20.0, min), 6);   // the default survives
        }

        // ----- the decision -----

        [Fact]
        public void Room_to_spare_means_do_nothing()
        {
            Assert.Equal(StorageAction.Nothing,
                StoragePolicy.Decide(795.0, 20.0, LowSpacePolicy.DeleteOldestContext, true));
            Assert.Equal(StorageAction.Nothing,
                StoragePolicy.Decide(20.0, 20.0, LowSpacePolicy.StopRecording, false));
        }

        [Fact]
        public void Below_the_reserve_each_policy_does_what_it_says()
        {
            Assert.Equal(StorageAction.DeleteOldest,
                StoragePolicy.Decide(19.0, 20.0, LowSpacePolicy.DeleteOldestContext, true));
            Assert.Equal(StorageAction.StopRecording,
                StoragePolicy.Decide(19.0, 20.0, LowSpacePolicy.StopRecording, true));
        }

        /// <summary>
        /// The case the two policies converge on. With no context left to delete, "delete the
        /// oldest" cannot help - and what filled the disk was the evidence, which this policy may
        /// not touch. Stopping and saying so is the only honest answer.
        /// </summary>
        [Fact]
        public void With_nothing_left_to_delete_even_the_deleting_policy_stops()
        {
            Assert.Equal(StorageAction.StopRecording,
                StoragePolicy.Decide(1.0, 20.0, LowSpacePolicy.DeleteOldestContext, false));
        }

        /// <summary>
        /// Deleting to exactly the reserve puts the next tick back over the line, and the policy
        /// becomes a treadmill of one file per tick. The margin makes it delete a batch and stop.
        /// </summary>
        [Fact]
        public void It_frees_past_the_reserve_so_it_does_not_run_every_tick()
        {
            double target = StoragePolicy.TargetGb(20.0);

            Assert.True(target > 20.0);
            Assert.Equal(StorageAction.Nothing,
                StoragePolicy.Decide(target, 20.0, LowSpacePolicy.DeleteOldestContext, true));
        }

        [Fact]
        public void The_status_line_says_the_room_and_what_happens_when_it_runs_out()
        {
            string ok = StoragePolicy.Describe(795.0, 20.0, LowSpacePolicy.DeleteOldestContext);
            Assert.Contains("795 GB free", ok);
            Assert.Contains("oldest context is deleted", ok);
            Assert.DoesNotContain("below", ok);

            string low = StoragePolicy.Describe(3.0, 20.0, LowSpacePolicy.StopRecording);
            Assert.Contains("below the 20 GB reserve", low);
            Assert.Contains("the recording stops", low);
        }
    }
}
